using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using FetchIt.Models;

namespace FetchIt.Services;

internal static class HttpFetch
{
    /// <summary>
    /// How long a download may receive nothing before it is cut and tried again.
    /// Without it a dead connection holds its slot until the user presses Stop.
    /// </summary>
    internal static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(45);

    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    internal static SocketsHttpHandler CreateHandler(
        int maxConnectionsPerServer = 4,
        TimeSpan? pooledLifetime = null,
        DecompressionMethods decompression = DecompressionMethods.None)
        => new()
        {
            AutomaticDecompression = decompression,
            MaxConnectionsPerServer = maxConnectionsPerServer,
            PooledConnectionLifetime = pooledLifetime ?? TimeSpan.FromMinutes(2)
        };

    internal static HttpClient CreateClient(
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        bool http11 = false)
    {
        var http = handler is null
            ? new HttpClient { Timeout = timeout ?? Timeout.InfiniteTimeSpan }
            : new HttpClient(handler) { Timeout = timeout ?? Timeout.InfiniteTimeSpan };
        if (http11)
        {
            http.DefaultRequestVersion = HttpVersion.Version11;
            http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return http;
    }

    internal static async Task SaveStreamAsync(
        HttpClient http,
        string url,
        string dest,
        CancellationToken cancellationToken,
        IProgress<FetchProgress>? progress = null,
        int retries = 3,
        int retryDelayMs = 400,
        bool resume = false,
        bool http11 = false,
        Action<HttpRequestMessage>? configure = null,
        TimeSpan? idleLimit = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(dest));
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var part = dest + ".part";
        Exception? last = null;
        for (var attempt = 0; attempt < retries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(retryDelayMs * attempt, cancellationToken).ConfigureAwait(false);
            try
            {
                await SaveOnceAsync(
                    http, url, dest, part, progress, resume, http11, configure, idleLimit ?? IdleLimit, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (!resume)
                    TryDelete(part);
            }
        }

        throw last ?? new InvalidOperationException("Could not save that file.");
    }

    internal static bool ShouldResume(string partPath, out long have)
    {
        have = 0;
        if (!File.Exists(partPath))
            return false;
        have = new FileInfo(partPath).Length;
        return have > 0;
    }

    private static async Task SaveOnceAsync(
        HttpClient http,
        string url,
        string dest,
        string part,
        IProgress<FetchProgress>? progress,
        bool resume,
        bool http11,
        Action<HttpRequestMessage>? configure,
        TimeSpan idleLimit,
        CancellationToken cancellationToken)
    {
        // Cancelled by silence, not by the user: the caller sees a failed try and repeats it.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleLimit);
        try
        {
            await CopyOnceAsync(http, url, dest, part, progress, resume, http11, configure, idleLimit, idle, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!resume)
                TryDelete(part);
            throw new IOException(QuietMessage);
        }
    }

    private static async Task CopyOnceAsync(
        HttpClient http,
        string url,
        string dest,
        string part,
        IProgress<FetchProgress>? progress,
        bool resume,
        bool http11,
        Action<HttpRequestMessage>? configure,
        TimeSpan idleLimit,
        CancellationTokenSource idle,
        CancellationToken cancellationToken)
    {
        var have = resume && ShouldResume(part, out var existing) ? existing : 0L;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (http11)
        {
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        var referer = ThumbnailUrl.RefererFor(url);
        if (referer is not null)
            request.Headers.Referrer = referer;
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);
        configure?.Invoke(request);

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token)
            .ConfigureAwait(false);
        if (resume && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDelete(part);
            throw new HttpRequestException("HTTP 416", null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();

        var append = resume && response.StatusCode == HttpStatusCode.PartialContent && have > 0;
        if (!append)
            have = 0;

        var total = response.Content.Headers.ContentLength is { } length
            ? (append ? have + length : length)
            : (long?)null;

        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBuffer);
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                await using var output = append
                    ? new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None)
                    : File.Create(part);
                var meter = new DownloadMeter();
                meter.Reset(have);
                long written = have;
                var lastReport = 0L;
                while (true)
                {
                    idle.CancelAfter(idleLimit);
                    var read = await input.ReadAsync(buffer.AsMemory(0, CopyBuffer), idle.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    if (progress is null || written - lastReport < CopyBuffer)
                        continue;
                    lastReport = written;
                    progress.Report(meter.Snapshot(written, total));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // The bytes are here: nothing below waits on the network.
            idle.CancelAfter(Timeout.InfiniteTimeSpan);

            var fileWritten = new FileInfo(part).Length;
            if (fileWritten == 0)
            {
                TryDelete(part);
                throw new InvalidOperationException("Could not save that file.");
            }

            if (resume && total is { } expected && fileWritten < expected)
                throw new IOException("Incomplete download.");

            File.Move(part, dest, overwrite: true);
            progress?.Report(new FetchProgress
            {
                Percent = 100,
                HasPercent = true,
                Status = "Saved"
            });
        }
        catch
        {
            if (!resume)
                TryDelete(part);
            throw;
        }
    }

    private const int CopyBuffer = 256 * 1024;

    internal const string QuietMessage = "The connection went quiet. Try again.";

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
