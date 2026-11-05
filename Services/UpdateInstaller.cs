using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace FetchIt.Services;

/// <summary>
/// What the updater does to the outside world once a verified file is ready:
/// start a process, close this app, and where the app lives. Tests swap these.
/// </summary>
public sealed class UpdateHost
{
    /// <summary>Start a process; false when it did not start.</summary>
    public Func<ProcessStartInfo, bool> Launch { get; init; } = DefaultLaunch;

    /// <summary>Ask the app to close. Must be safe to call from any thread.</summary>
    public Action RequestShutdown { get; init; } = DefaultRequestShutdown;

    /// <summary>
    /// Start Setup and watch it briefly; false when it did not start or died
    /// right away. Must be true before this app closes for the update.
    /// </summary>
    public Func<ProcessStartInfo, Task<bool>> LaunchSetup { get; init; }
        = start => WatchSetupAsync(start, SetupGrace);

    /// <summary>A blocked Setup exits within a second; a working one runs much longer.</summary>
    public static readonly TimeSpan SetupGrace = TimeSpan.FromSeconds(5);

    internal static async Task<bool> WatchSetupAsync(ProcessStartInfo start, TimeSpan grace)
    {
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not start {Path.GetFileName(start.FileName)}: {ex.Message}");
            return false;
        }

        if (process is null)
            return false;

        using (process)
        {
            using var timeout = new CancellationTokenSource(grace);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return true; // Still running: Setup got past the point where it can be blocked.
            }

            if (process.ExitCode == 0)
                return true;

            // Inno exit code 1 = "failed to initialize", which includes antivirus
            // rules that stop its bootstrap .tmp from running in %TEMP%.
            AppLog.Warn($"Setup exited with code {process.ExitCode} before installing. Antivirus rules can cause this.");
            return false;
        }
    }

    public string TargetDirectory { get; init; } = AppContext.BaseDirectory;

    public static UpdateHost Default { get; } = new();

    private static bool DefaultLaunch(ProcessStartInfo start)
    {
        try
        {
            return Process.Start(start) is not null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not start {Path.GetFileName(start.FileName)}: {ex.Message}");
            return false;
        }
    }

    // The install steps run after ConfigureAwait(false), i.e. on a pool thread.
    // Avalonia only lets the UI thread shut the app down, so always post there.
    private static void DefaultRequestShutdown()
        => Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });
}

public sealed class UpdateInstaller : IDisposable
{
    public const long DefaultMinBytes = 1_000_000;
    public const long DefaultMaxBytes = 400L * 1024 * 1024;
    public const string SilentArgs = "/SILENT /NORESTART /SUPPRESSMSGBOXES";
    public const string SumsFileName = "SHA256SUMS.txt";
    public const string VerificationFailedMessage = "Update file failed verification.";
    public const string NoSumsMessage = "That release has no SHA256SUMS.txt, so it cannot be verified.";
    public const string RestartManuallyMessage = "Update installed. Close fetch it and open it again.";

    private readonly HttpClient _http;
    private readonly long _minBytes;
    private readonly long _maxBytes;
    private readonly UpdateHost _host;

    public UpdateInstaller(
        HttpMessageHandler? handler = null,
        long minBytes = DefaultMinBytes,
        long maxBytes = DefaultMaxBytes,
        UpdateHost? host = null)
    {
        _host = host ?? UpdateHost.Default;
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

    /// <summary>True when this copy was put here by the Inno Setup installer.</summary>
    public static bool IsInstalledWithSetup(string? dir = null)
    {
        try
        {
            var root = dir ?? AppContext.BaseDirectory;
            return File.Exists(Path.Combine(root, "unins000.exe"))
                   || File.Exists(Path.Combine(root, "unins000.dat"));
        }
        catch
        {
            return false;
        }
    }

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

    /// <summary>Parse a sha256sum-style file: "&lt;hex&gt;  name" or "&lt;hex&gt; *name" per line.</summary>
    internal static IReadOnlyDictionary<string, string> ParseSums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66 || line.StartsWith('#'))
                continue;
            var hex = line[..64];
            if (!hex.All(Uri.IsHexDigit))
                continue;
            var name = line[64..].TrimStart(' ', '\t', '*');
            name = Path.GetFileName(name.Trim());
            if (name.Length > 0)
                sums[name] = hex.ToLowerInvariant();
        }

        return sums;
    }

    internal static string? ExpectedSha256(IReadOnlyDictionary<string, string> sums, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        return sums.TryGetValue(Path.GetFileName(uri.AbsolutePath), out var hex) ? hex : null;
    }

    internal static bool Sha256Matches(string path, string expectedHex)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> DownloadSumsAsync(string url, CancellationToken cancellationToken)
    {
        if (!GitHubUpdateClient.IsAllowedHttpsUrl(url))
            throw new InvalidOperationException("That checksum file is not on GitHub.");
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        if (!GitHubUpdateClient.IsAllowedHttpsUrl(finalUrl))
            throw new InvalidOperationException("Checksum download left GitHub.");
        if (response.Content.Headers.ContentLength is > 64 * 1024)
            throw new InvalidOperationException("Checksum file looks wrong.");
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var sums = ParseSums(text);
        if (sums.Count == 0)
            throw new InvalidOperationException("Checksum file is empty.");
        return sums;
    }

    public Task<string> DownloadSetupAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? expectedSha256 = null)
    {
        if (!IsSetupDownload(url))
            throw new InvalidOperationException("That update file is not a GitHub setup.");
        return DownloadToAsync(url, StagingPath(), progress, cancellationToken, expectedSha256, requirePe: true);
    }

    public Task<string> DownloadZipAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? expectedSha256 = null)
    {
        if (!IsZipDownload(url))
            throw new InvalidOperationException("That update file is not a GitHub zip.");
        return DownloadToAsync(url, StagingZipPath(), progress, cancellationToken, expectedSha256, requirePe: false);
    }

    private async Task<string> DownloadToAsync(
        string url,
        string dest,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? expectedSha256,
        bool requirePe)
    {
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

            if (expectedSha256 is not null && !Sha256Matches(tmp, expectedSha256))
            {
                AppLog.Error($"Update {Path.GetFileName(dest)} did not match SHA256SUMS.");
                throw new InvalidOperationException(VerificationFailedMessage);
            }

            File.Move(tmp, dest, overwrite: true);
            if (requirePe && !LooksLikePe(dest))
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

    /// <summary>
    /// Start the new FetchIt.exe and close this one. Returns false when the new
    /// process did not start; the files are already in place either way.
    /// </summary>
    internal bool RestartFrom(string targetDir)
    {
        var exe = Path.Combine(targetDir, "FetchIt.exe");
        if (!File.Exists(exe))
            return false;

        // Release the single-instance lock first so the new process can own it.
        App.SingleInstance?.Dispose();
        App.SingleInstance = null;
        var started = _host.Launch(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            WorkingDirectory = targetDir
        });
        if (started)
            _host.RequestShutdown();
        return started;
    }

    public static bool StartSetup(string path)
        => SetupStart(path) is { } start && UpdateHost.Default.Launch(start);

    /// <summary>How to run the staged setup, or null when the path fails its checks.</summary>
    internal static ProcessStartInfo? SetupStart(string path)
    {
        if (!IsSafeSetupPath(path) || !LooksLikePe(path))
            return null;

        return new ProcessStartInfo
        {
            FileName = path,
            Arguments = SilentArgs,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path)
        };
    }

    /// <summary>Close the app from any thread.</summary>
    public static void ShutdownApp() => UpdateHost.Default.RequestShutdown();

    /// <summary>
    /// Download, verify and start the setup. Returns false when Setup did not
    /// start or was stopped at once (Defender ASR rules can block its bootstrap),
    /// so the caller may still try another asset. Once Setup runs, this app must
    /// close and nothing else may touch the install folder.
    /// </summary>
    internal async Task<bool> TryInstallSetupAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? expectedSha256)
    {
        var path = await DownloadSetupAsync(url, progress, cancellationToken, expectedSha256).ConfigureAwait(false);
        var start = SetupStart(path);
        if (start is null || !await _host.LaunchSetup(start).ConfigureAwait(false))
        {
            AppLog.Warn("Setup did not run.");
            return false;
        }

        AppLog.Info("Setup running; closing for the update.");
        _host.RequestShutdown();
        return true;
    }

    /// <summary>
    /// Download, verify and apply the zip, then restart. Once ApplyZip begins the
    /// install folder has changed, so failures after that point are final.
    /// </summary>
    internal async Task InstallZipCoreAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? expectedSha256)
    {
        var zip = await DownloadZipAsync(url, progress, cancellationToken, expectedSha256).ConfigureAwait(false);
        var dir = _host.TargetDirectory;
        ApplyZip(zip, dir);
        TryDelete(zip);
        AppLog.Info("Update zip applied; restarting.");
        if (!RestartFrom(dir))
            throw new InvalidOperationException(RestartManuallyMessage);
    }

    public async Task InstallSetupAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? expectedSha256 = null)
    {
        if (!await TryInstallSetupAsync(url, progress, cancellationToken, expectedSha256).ConfigureAwait(false))
            throw new InvalidOperationException("Could not start the installer.");
    }

    public Task InstallZipAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? expectedSha256 = null)
        => InstallZipCoreAsync(url, progress, cancellationToken, expectedSha256);

    /// <summary>
    /// Pick the install path. A copy placed by Setup updates through Setup so
    /// Settings → Apps keeps the right version; a portable copy updates in place.
    /// </summary>
    internal static IReadOnlyList<string> PlanOrder(UpdateCheckResult result, bool installedWithSetup)
    {
        var setup = result.DownloadUrl is not null && IsSetupDownload(result.DownloadUrl) ? result.DownloadUrl : null;
        var zip = result.ZipUrl is not null && IsZipDownload(result.ZipUrl)
            ? result.ZipUrl
            : result.DownloadUrl is not null && IsZipDownload(result.DownloadUrl) ? result.DownloadUrl : null;

        var order = new List<string>(2);
        if (installedWithSetup)
        {
            if (setup is not null) order.Add(setup);
            if (zip is not null) order.Add(zip);
        }
        else
        {
            if (zip is not null) order.Add(zip);
            if (setup is not null) order.Add(setup);
        }

        return order;
    }

    public async Task InstallAsync(
        UpdateCheckResult result,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var order = PlanOrder(result, IsInstalledWithSetup(_host.TargetDirectory));
        if (order.Count == 0)
            throw new InvalidOperationException("No compatible installer found for this release.");

        if (result.SumsUrl is null)
            throw new InvalidOperationException(NoSumsMessage);
        var sums = await DownloadSumsAsync(result.SumsUrl, cancellationToken).ConfigureAwait(false);

        // Fall back to the next asset only while nothing has been changed:
        // a failed download, or a setup that never started. A setup that runs,
        // or a zip that starts unpacking, is the update; never stack another on it.
        Exception? last = null;
        foreach (var url in order)
        {
            var expected = ExpectedSha256(sums, url)
                ?? throw new InvalidOperationException(NoSumsMessage);
            var name = Path.GetFileName(new Uri(url).AbsolutePath);

            if (IsZipDownload(url))
            {
                string zip;
                try
                {
                    zip = await DownloadZipAsync(url, progress, cancellationToken, expected).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRetryableDownloadFailure(ex))
                {
                    AppLog.Warn($"Update via {name} failed to download: {ex.Message}");
                    last = ex;
                    continue;
                }

                ApplyZip(zip, _host.TargetDirectory);
                TryDelete(zip);
                AppLog.Info("Update zip applied; restarting.");
                if (!RestartFrom(_host.TargetDirectory))
                    throw new InvalidOperationException(RestartManuallyMessage);
                return;
            }

            try
            {
                if (await TryInstallSetupAsync(url, progress, cancellationToken, expected).ConfigureAwait(false))
                    return;
                last = new InvalidOperationException("Could not start the installer.");
            }
            catch (Exception ex) when (IsRetryableDownloadFailure(ex))
            {
                AppLog.Warn($"Update via {name} failed to download: {ex.Message}");
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("Could not install the update.");
    }

    /// <summary>A failure before anything ran or changed; a tampered file is never retried.</summary>
    private static bool IsRetryableDownloadFailure(Exception ex)
        => ex is not OperationCanceledException
           && !(ex is InvalidOperationException { Message: VerificationFailedMessage });

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
