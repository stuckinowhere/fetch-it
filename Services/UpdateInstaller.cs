using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace FetchIt.Services;

public sealed class UpdateInstaller : IDisposable
{
    public const long DefaultMinBytes = 1_000_000;
    public const long DefaultMaxBytes = 400L * 1024 * 1024;
    public const string SilentArgs = "/SILENT /NORESTART /SUPPRESSMSGBOXES";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly long _minBytes;
    private readonly long _maxBytes;

    public UpdateInstaller(
        HttpMessageHandler? handler = null,
        long minBytes = DefaultMinBytes,
        long maxBytes = DefaultMaxBytes)
    {
        _minBytes = minBytes;
        _maxBytes = maxBytes;
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromMinutes(10) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttp = true;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WasdFetchIt-Updater");
    }

    public static bool IsSetupDownload(string? url)
    {
        if (!GitHubUpdateClient.IsAllowedHttpsUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var name = Path.GetFileName(uri.AbsolutePath);
        return name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static string StagingPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WasdFetchIt");
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(Path.Combine(dir, "fetch-it-setup.exe"));
    }

    public static bool IsSafeSetupPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WasdFetchIt"))
                   .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               && File.Exists(full);
    }

    public static bool LooksLikePe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == (byte)'M' && stream.ReadByte() == (byte)'Z';
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> DownloadSetupAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsSetupDownload(url))
            throw new InvalidOperationException("That update file is not a GitHub setup.");

        var dest = StagingPath();
        var tmp = dest + ".part";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var finalUrl = response.RequestMessage?.RequestUri?.ToString();
            if (!GitHubUpdateClient.IsAllowedHttpsUrl(finalUrl))
                throw new InvalidOperationException("Update download left GitHub.");

            var length = response.Content.Headers.ContentLength;
            if (length is { } known && (known < _minBytes || known > _maxBytes))
                throw new InvalidOperationException("Update file size looks wrong.");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    if (copied > _maxBytes)
                        throw new InvalidOperationException("Update file is too large.");
                    if (length is { } total && total > 0)
                        progress?.Report(100.0 * copied / total);
                }

                if (copied < _minBytes)
                    throw new InvalidOperationException("Update file is too small.");
            }

            File.Move(tmp, dest, overwrite: true);
            if (!LooksLikePe(dest))
            {
                TryDelete(dest);
                throw new InvalidOperationException("Update file is not an installer.");
            }

            return dest;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static bool StartSetup(string path)
    {
        if (!IsSafeSetupPath(path) || !LooksLikePe(path))
            return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = SilentArgs,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path)
        });
        return true;
    }

    public static void ShutdownApp()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public async Task InstallAsync(string url, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var path = await DownloadSetupAsync(url, progress, cancellationToken).ConfigureAwait(true);
        if (!StartSetup(path))
            throw new InvalidOperationException("Could not start the installer.");
        ShutdownApp();
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
