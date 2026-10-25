using System.Diagnostics;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace FetchIt.Services;

public sealed class UpdateInstaller : IDisposable
{
    public const long DefaultMinBytes = 1_000_000;
    public const long DefaultMaxBytes = 400L * 1024 * 1024;
    public const string SilentArgs = "/SILENT /NORESTART /SUPPRESSMSGBOXES";

    private readonly HttpClient _http;
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

    public static bool IsZipDownload(string? url)
    {
        if (!GitHubUpdateClient.IsAllowedHttpsUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var name = Path.GetFileName(uri.AbsolutePath);
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedDownload(string? url)
        => IsSetupDownload(url) || IsZipDownload(url);

    public static string StagingPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WasdFetchIt");
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(Path.Combine(dir, "fetch-it-setup.exe"));
    }

    public static string StagingZipPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WasdFetchIt");
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(Path.Combine(dir, "fetch-it-update.zip"));
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

    public async Task<string> DownloadZipAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsZipDownload(url))
            throw new InvalidOperationException("That update file is not a GitHub zip.");

        var dest = StagingZipPath();
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
            return dest;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static void ApplyZip(string zipPath, string targetDir)
    {
        var targetRoot = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!dest.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            var folder = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            if (File.Exists(dest))
            {
                try
                {
                    entry.ExtractToFile(dest, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var old = dest + ".old";
                    try { File.Delete(old); } catch { }
                    if (File.Exists(old))
                        old = dest + "." + Guid.NewGuid().ToString("N")[..6] + ".old";
                    File.Move(dest, old);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
            else
            {
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
    }

    public static void RestartFrom(string targetDir)
    {
        var exe = Path.Combine(targetDir, "FetchIt.exe");
        if (File.Exists(exe))
        {
            App.SingleInstance?.Dispose();
            App.SingleInstance = null;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = targetDir
            });
            ShutdownApp();
        }
    }

    public static bool StartSetup(string path)
    {
        if (!IsSafeSetupPath(path) || !LooksLikePe(path))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = SilentArgs,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path)
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ShutdownApp()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public async Task InstallSetupAsync(string url, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var path = await DownloadSetupAsync(url, progress, cancellationToken).ConfigureAwait(true);
        if (!StartSetup(path))
            throw new InvalidOperationException("Could not start the installer.");
        ShutdownApp();
    }

    public async Task InstallZipAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? targetDir = null)
    {
        var zip = await DownloadZipAsync(url, progress, cancellationToken).ConfigureAwait(false);
        var dir = targetDir ?? AppContext.BaseDirectory;
        ApplyZip(zip, dir);
        TryDelete(zip);
        RestartFrom(dir);
    }

    public async Task InstallAsync(
        UpdateCheckResult result,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (result.ZipUrl is not null && IsZipDownload(result.ZipUrl))
        {
            try
            {
                await InstallZipAsync(result.ZipUrl, progress, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch when (result.DownloadUrl is not null && IsSetupDownload(result.DownloadUrl))
            {
            }
        }

        if (result.DownloadUrl is not null && IsSetupDownload(result.DownloadUrl))
        {
            try
            {
                await InstallSetupAsync(result.DownloadUrl, progress, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch when (result.ZipUrl is not null && IsZipDownload(result.ZipUrl))
            {
                await InstallZipAsync(result.ZipUrl, progress, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (result.DownloadUrl is not null && IsZipDownload(result.DownloadUrl))
        {
            await InstallZipAsync(result.DownloadUrl, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("No compatible installer found for this release.");
    }

    public Task InstallAsync(string url, IProgress<double>? progress, CancellationToken cancellationToken)
        => IsZipDownload(url)
            ? InstallZipAsync(url, progress, cancellationToken)
            : InstallSetupAsync(url, progress, cancellationToken);

    public void Dispose() => _http.Dispose();

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
