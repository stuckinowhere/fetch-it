using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FetchIt.Services;

public sealed class ToolPaths
{
    public required string YtDlp { get; init; }
    public required string FfmpegDir { get; init; }

    /// <summary>Standalone gallery-dl.exe, or null when it is missing or fails its hash.</summary>
    public string? GalleryDl { get; init; }
}

public static class ToolBootstrapper
{
    internal static ToolPinTable Pins => ToolPins.Current;

    public static string LocalToolsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "tools");

    public static IEnumerable<string> CandidateRoots()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return LocalToolsDirectory;
        yield return Path.Combine(baseDir, "tools");
    }

    internal const long MinFfmpegBytes = 8L * 1024 * 1024;

    private static readonly SemaphoreSlim EnsureGate = new(1, 1);
    private static DateTime _galleryFailedAt = DateTime.MinValue;
    private static readonly TimeSpan GalleryRetryAfter = TimeSpan.FromMinutes(10);

    internal static bool IsUsableFfmpeg(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= MinFfmpegBytes;
        }
        catch
        {
            return false;
        }
    }

    public static ToolPaths? TryFind() => TryFind(Pins);

    internal static ToolPaths? TryFind(ToolPinTable pins)
    {
        foreach (var root in CandidateRoots())
        {
            var yt = Path.Combine(root, "yt-dlp.exe");
            if (!File.Exists(yt) || !Sha256Equals(yt, pins.YtDlp.Sha256))
                continue;

            var ffmpeg = Path.Combine(root, "ffmpeg.exe");
            if (!IsUsableFfmpeg(ffmpeg))
                continue;

            var gallery = Path.Combine(root, "gallery-dl.exe");
            if (!File.Exists(gallery) || !Sha256Equals(gallery, pins.GalleryDlExe.Sha256))
                gallery = FindGalleryElsewhere(pins);

            return new ToolPaths
            {
                YtDlp = yt,
                FfmpegDir = root,
                GalleryDl = gallery
            };
        }

        return null;
    }

    private static string? FindGalleryElsewhere(ToolPinTable pins)
    {
        foreach (var root in CandidateRoots())
        {
            var gallery = Path.Combine(root, "gallery-dl.exe");
            if (File.Exists(gallery) && Sha256Equals(gallery, pins.GalleryDlExe.Sha256))
                return gallery;
        }

        return null;
    }

    public static async Task<ToolPaths> EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var existing = TryFind();
        if (existing is not null && (existing.GalleryDl is not null || !ShouldRetryGallery()))
            return existing;

        await EnsureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = TryFind();
            if (existing is not null && (existing.GalleryDl is not null || !ShouldRetryGallery()))
                return existing;

            try
            {
                return await EnsureCoreAsync(Pins, existing, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
                when (ex.Message.StartsWith("Hash mismatch", StringComparison.Ordinal)
                      && !ReferenceEquals(Pins, ToolPins.Embedded))
            {
                // A remote pin that does not match its download must not brick the app:
                // fall back to the pins compiled into this build.
                AppLog.Warn($"Remote tool pins rejected: {ex.Message}. Using embedded pins.");
                ToolPins.ResetToEmbedded();
                return await EnsureCoreAsync(Pins, TryFind(), progress, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            EnsureGate.Release();
        }
    }

    private static bool ShouldRetryGallery() => DateTime.UtcNow - _galleryFailedAt >= GalleryRetryAfter;

    private static async Task<ToolPaths> EnsureCoreAsync(
        ToolPinTable pins,
        ToolPaths? existing,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var dest = LocalToolsDirectory;
        Directory.CreateDirectory(dest);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };

        progress?.Report("tools");
        if (existing is null)
        {
            await DownloadVerifiedAsync(http, pins.YtDlp.Url, Path.Combine(dest, "yt-dlp.exe"), pins.YtDlp.Sha256, cancellationToken)
                .ConfigureAwait(false);

            var ffmpeg = Path.Combine(dest, "ffmpeg.exe");
            if (!IsUsableFfmpeg(ffmpeg))
            {
                var zipPath = Path.Combine(dest, "ffmpeg.zip");
                await DownloadVerifiedAsync(http, pins.FfmpegZip.Url, zipPath, pins.FfmpegZip.Sha256, cancellationToken)
                    .ConfigureAwait(false);
                ExtractFfmpeg(zipPath, dest);
                TryDelete(zipPath);
            }
        }

        try
        {
            await DownloadVerifiedAsync(http, pins.GalleryDlExe.Url, Path.Combine(dest, "gallery-dl.exe"), pins.GalleryDlExe.Sha256, cancellationToken)
                .ConfigureAwait(false);
            _galleryFailedAt = DateTime.MinValue;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // yt-dlp still works without gallery-dl; try again later instead of on every fetch.
            _galleryFailedAt = DateTime.UtcNow;
            AppLog.Warn($"gallery-dl download failed: {ex.Message}");
        }

        return TryFind(pins) ?? existing ?? throw new InvalidOperationException("Could not install yt-dlp or ffmpeg.");
    }

    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Hash)> HashCache = new();

    internal static bool Sha256Equals(string path, string expectedHex)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            var key = info.FullName;
            if (HashCache.TryGetValue(key, out var cached)
                && cached.Length == info.Length
                && cached.Written == info.LastWriteTimeUtc)
            {
                return cached.Hash.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
            }

            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            HashCache[key] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void ExtractFfmpeg(string zipPath, string dest)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)
                || e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                continue;
            entry.ExtractToFile(Path.Combine(dest, name), overwrite: true);
        }
    }

    private static async Task DownloadVerifiedAsync(
        HttpClient http,
        string url,
        string dest,
        string sha256,
        CancellationToken cancellationToken)
    {
        if (File.Exists(dest) && Sha256Equals(dest, sha256))
            return;

        if (!ToolPins.IsAllowedToolUrl(url))
            throw new InvalidOperationException($"Tool URL not allowed: {url}");

        AppLog.Info($"download {url}");
        var tmp = dest + ".part";
        TryDelete(tmp);
        await using (var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
        await using (var file = File.Create(tmp))
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

        if (!Sha256Equals(tmp, sha256))
        {
            TryDelete(tmp);
            throw new InvalidOperationException($"Hash mismatch for {Path.GetFileName(dest)}.");
        }

        File.Move(tmp, dest, overwrite: true);
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

internal sealed class ToolPin
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

internal sealed class ToolPinTable
{
    internal const string ResourceName = "FetchIt.tool-pins.json";

    [JsonPropertyName("ytDlp")]
    public required ToolPin YtDlp { get; init; }

    [JsonPropertyName("ffmpegZip")]
    public required ToolPin FfmpegZip { get; init; }

    [JsonPropertyName("galleryDlExe")]
    public required ToolPin GalleryDlExe { get; init; }

    internal IEnumerable<ToolPin> All => [YtDlp, FfmpegZip, GalleryDlExe];

    internal static ToolPinTable Load()
    {
        using var stream = typeof(ToolBootstrapper).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded {ResourceName}.");
        return Parse(stream);
    }

    internal static ToolPinTable Parse(Stream json)
    {
        using var reader = new StreamReader(json);
        return Parse(reader.ReadToEnd());
    }

    internal static ToolPinTable Parse(string json)
    {
        ToolPinTable? table;
        try
        {
            table = JsonSerializer.Deserialize<ToolPinTable>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid tool-pins.json: " + ex.Message, ex);
        }

        if (table is null)
            throw new InvalidOperationException("Invalid tool-pins.json.");
        table.Validate();
        return table;
    }

    private void Validate()
    {
        if (YtDlp is null || FfmpegZip is null || GalleryDlExe is null)
            throw new InvalidOperationException("tool-pins.json is missing a pin.");
        foreach (var pin in All)
        {
            if (string.IsNullOrWhiteSpace(pin.Url) || string.IsNullOrWhiteSpace(pin.Sha256))
                throw new InvalidOperationException("tool-pins.json is missing a url or sha256.");
            if (pin.Sha256.Length != 64 || !pin.Sha256.All(Uri.IsHexDigit))
                throw new InvalidOperationException("tool-pins.json has a malformed sha256.");
            if (!ToolPins.IsAllowedToolUrl(pin.Url))
                throw new InvalidOperationException($"tool-pins.json URL not allowed: {pin.Url}");
        }
    }

    internal bool SameAs(ToolPinTable other)
        => All.Zip(other.All).All(pair =>
            pair.First.Url == pair.Second.Url
            && pair.First.Sha256.Equals(pair.Second.Sha256, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Tool pins come from the embedded table, optionally overridden by a newer
/// copy of scripts/tool-pins.json fetched from this repository. That lets a
/// yt-dlp bump reach users without shipping a new app build.
/// </summary>
internal static class ToolPins
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/stuckinowhere/fetch-it/main/scripts/tool-pins.json";

    internal static readonly ToolPinTable Embedded = ToolPinTable.Load();
    private static volatile ToolPinTable _current = LoadCachedOrEmbedded();

    public static ToolPinTable Current => _current;

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "tool-pins.json");

    internal static bool IsAllowedToolUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return host is "github.com"
            or "objects.githubusercontent.com"
            or "release-assets.githubusercontent.com"
            or "codeberg.org"
            or "files.pythonhosted.org";
    }

    private static ToolPinTable LoadCachedOrEmbedded()
    {
        try
        {
            if (File.Exists(CachePath))
                return ToolPinTable.Parse(File.ReadAllText(CachePath));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Ignoring cached tool pins: {ex.Message}");
        }

        return Embedded;
    }

    internal static void ResetToEmbedded()
    {
        _current = Embedded;
        try
        {
            if (File.Exists(CachePath))
                File.Delete(CachePath);
        }
        catch
        {
        }
    }

    /// <summary>Fetch the latest pins; never throws. Returns true when pins changed.</summary>
    public static async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WasdFetchIt-Pins");
            var json = await http.GetStringAsync(RemoteUrl, cancellationToken).ConfigureAwait(false);
            return Apply(json);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Tool pin refresh skipped: {ex.Message}");
            return false;
        }
    }

    internal static bool Apply(string json)
    {
        var table = ToolPinTable.Parse(json);
        if (table.SameAs(_current))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, json);
        _current = table;
        AppLog.Info("Tool pins updated from remote.");
        return true;
    }
}
