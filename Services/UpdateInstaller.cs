using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace FetchIt.Services;

public sealed class UpdateInstaller : IDisposable
{
    public const long DefaultMinBytes = 1_000_000;
    public const long DefaultMaxBytes = 400L * 1024 * 1024;
    public const string SilentArgs = "/SILENT /NORESTART /SUPPRESSMSGBOXES";
    public const string SumsFileName = "SHA256SUMS.txt";
    public const string VerificationFailedMessage = "Update file failed verification.";
    public const string NoSumsMessage = "That release has no SHA256SUMS.txt, so it cannot be verified.";

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

    public async Task InstallSetupAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? expectedSha256 = null)
    {
        var path = await DownloadSetupAsync(url, progress, cancellationToken, expectedSha256).ConfigureAwait(true);
        if (!StartSetup(path))
            throw new InvalidOperationException("Could not start the installer.");
        ShutdownApp();
    }

    public async Task InstallZipAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string? targetDir = null,
        string? expectedSha256 = null)
    {
        var zip = await DownloadZipAsync(url, progress, cancellationToken, expectedSha256).ConfigureAwait(false);
        var dir = targetDir ?? AppContext.BaseDirectory;
        ApplyZip(zip, dir);
        TryDelete(zip);
        RestartFrom(dir);
    }

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
        var order = PlanOrder(result, IsInstalledWithSetup());
        if (order.Count == 0)
            throw new InvalidOperationException("No compatible installer found for this release.");

        if (result.SumsUrl is null)
            throw new InvalidOperationException(NoSumsMessage);
        var sums = await DownloadSumsAsync(result.SumsUrl, cancellationToken).ConfigureAwait(false);

        Exception? last = null;
        foreach (var url in order)
        {
            var expected = ExpectedSha256(sums, url)
                ?? throw new InvalidOperationException(NoSumsMessage);
            try
            {
                if (IsZipDownload(url))
                    await InstallZipAsync(url, progress, cancellationToken, expectedSha256: expected).ConfigureAwait(false);
                else
                    await InstallSetupAsync(url, progress, cancellationToken, expected).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (ex.Message == VerificationFailedMessage)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Update via {Path.GetFileName(new Uri(url).AbsolutePath)} failed: {ex.Message}");
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("Could not install the update.");
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
