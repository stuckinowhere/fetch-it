using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FetchIt.Models;

namespace FetchIt.Services;

internal static class GofileCdn
{
    public static string CurlPath => Path.Combine(Environment.SystemDirectory, "curl.exe");

    public static async Task SaveAsync(
        string url,
        string dest,
        string token,
        CancellationToken cancellationToken,
        IProgress<FetchProgress>? progress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
        if (await TryHttpAsync(url, dest, token, progress, cancellationToken).ConfigureAwait(false))
            return;
        progress?.Report(new FetchProgress { Status = "Saving…", HasPercent = false });
        if (await TryCurlAsync(url, dest, token, cancellationToken).ConfigureAwait(false))
            return;
        throw new InvalidOperationException("Could not save those files.");
    }

    internal static IReadOnlyList<string> CurlArgs(string url, string dest, string token)
    {
        var args = new List<string>
        {
            "-fL",
            "--http1.1",
            "-4",
            "--ssl-no-revoke",
            "--retry", "8",
            "--retry-delay", "2",
            "--connect-timeout", "45",
            "-C", "-",
            "-A", GofileService.UserAgent,
            "-e", "https://gofile.io/",
            "-H", "Origin: https://gofile.io",
            "-H", "Accept: */*",
            "-o", dest
        };
        if (!string.IsNullOrEmpty(token))
        {
            args.Add("-H");
            args.Add("Authorization: Bearer " + token);
            args.Add("-b");
            args.Add("accountToken=" + token);
        }

        args.Add(url);
        return args;
    }

    internal static IReadOnlyList<string> CurlApiArgs(string url, string? token, string? websiteToken)
    {
        var args = new List<string>
        {
            "-sS",
            "--http1.1",
            "-4",
            "--ssl-no-revoke",
            "--connect-timeout", "2",
            "--max-time", "8",
            "-A", GofileService.UserAgent,
            "-e", "https://gofile.io/",
            "-H", "Origin: https://gofile.io",
            "-H", "Accept: application/json"
        };
        if (!string.IsNullOrEmpty(token))
        {
            args.Add("-H");
            args.Add("Authorization: Bearer " + token);
            args.Add("-H");
            args.Add("X-BL: en-US");
        }

        if (!string.IsNullOrEmpty(websiteToken))
        {
            args.Add("-H");
            args.Add("X-Website-Token: " + websiteToken);
        }

        args.Add(url);
        return args;
    }

    internal static async Task<bool> CanReachApiAsync(CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            using var dnsWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsWait.CancelAfter(TimeSpan.FromSeconds(2));
            addresses = await Dns.GetHostAddressesAsync("api.gofile.io", AddressFamily.InterNetwork, dnsWait.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (addresses.Length == 0)
            return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        foreach (var ip in addresses.Take(3))
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ip, 443, deadline.Token).ConfigureAwait(false);
                if (tcp.Connected)
                    return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return false;
    }

    internal static async Task<string?> ApiGetAsync(
        string url,
        string? token,
        string? websiteToken,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(CurlPath))
            return null;
        try
        {
            var text = await ProcessRunner.RunTextAsync(
                CurlPath,
                CurlApiArgs(url, token, websiteToken),
                cancellationToken,
                timeout: TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static bool ShouldResume(string partPath, out long have)
    {
        have = 0;
        if (!File.Exists(partPath))
            return false;
        have = new FileInfo(partPath).Length;
        return have > 0;
    }

    private static async Task<bool> TryHttpAsync(
        string url,
        string dest,
        string token,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var part = dest + ".part";
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false);
            try
            {
                using var handler = GofileService.CreateHandler(cookies: true);
                if (!string.IsNullOrEmpty(token))
                    handler.CookieContainer!.Add(new Cookie("accountToken", token, "/", ".gofile.io"));
                using var http = GofileService.CreateClient(handler, TimeSpan.FromMinutes(15));
                if (!string.IsNullOrEmpty(token))
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);

                var have = ShouldResume(part, out var existing) ? existing : 0;
                using var request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                request.Headers.ConnectionClose = true;
                if (have > 0)
                    request.Headers.Range = new RangeHeaderValue(have, null);

                using var response = await http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    TryDelete(part);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    continue;

                var append = response.StatusCode == HttpStatusCode.PartialContent && have > 0;
                if (!append)
                    have = 0;

                var total = response.Content.Headers.ContentLength is { } length
                    ? (append ? have + length : length)
                    : (long?)null;

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = append
                    ? new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None)
                    : File.Create(part))
                {
                    var meter = new DownloadMeter();
                    meter.Reset(have);
                    var buffer = new byte[256 * 1024];
                    long written = have;
                    var lastReport = 0L;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        written += read;
                        if (progress is null || written - lastReport < 256 * 1024)
                            continue;
                        lastReport = written;
                        progress.Report(meter.Snapshot(written, total));
                    }
                }

                var fileWritten = new FileInfo(part).Length;
                if (fileWritten == 0)
                    continue;
                if (total is { } expected && fileWritten < expected)
                    continue;

                File.Move(part, dest, overwrite: true);
                progress?.Report(new FetchProgress
                {
                    Percent = 100,
                    HasPercent = true,
                    Status = "Saved"
                });
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        return false;
    }

    private static async Task<bool> TryCurlAsync(
        string url,
        string dest,
        string token,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(CurlPath))
            return false;

        var part = dest + ".part";
        var code = await ProcessRunner.RunAsync(
            CurlPath,
            CurlArgs(url, part, token),
            onLine: null,
            cancellationToken).ConfigureAwait(false);
        if (code != 0 || !File.Exists(part) || new FileInfo(part).Length == 0)
            return false;
        File.Move(part, dest, overwrite: true);
        return true;
    }

    private static void TryDelete(string path)
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
