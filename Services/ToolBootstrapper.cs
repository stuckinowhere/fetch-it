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

    private static readonly SemaphoreSlim EnsureGate = new(1, 1);
    private static DateTime _galleryFailedAt = DateTime.MinValue;
    private static readonly TimeSpan GalleryRetryAfter = TimeSpan.FromMinutes(10);

    public static ToolPaths? TryFind() => TryFind(Pins, CandidateRoots());

    /// <summary>
    /// A root counts only when yt-dlp.exe, ffmpeg.exe and ffprobe.exe all match
    /// their pinned SHA-256: that directory is handed to yt-dlp as
    /// --ffmpeg-location, so every binary in it must be bound by hash.
    /// </summary>
    internal static ToolPaths? TryFind(ToolPinTable pins, IEnumerable<string> roots)
    {
        var rootList = roots.ToList();
        foreach (var root in rootList)
        {
            if (!HasVerifiedCore(root, pins))
                continue;

            var gallery = Path.Combine(root, "gallery-dl.exe");
            if (!Sha256Equals(gallery, pins.GalleryDlExe.Sha256))
                gallery = FindGalleryElsewhere(pins, rootList);

            return new ToolPaths
            {
                YtDlp = Path.Combine(root, "yt-dlp.exe"),
                FfmpegDir = root,
                GalleryDl = gallery
            };
        }

        return null;
    }

    internal static bool HasVerifiedCore(string root, ToolPinTable pins)
        => Sha256Equals(Path.Combine(root, "yt-dlp.exe"), pins.YtDlp.Sha256)
           && HasVerifiedFfmpeg(root, pins);

    internal static bool HasVerifiedFfmpeg(string root, ToolPinTable pins)
        => Sha256Equals(Path.Combine(root, "ffmpeg.exe"), pins.FfmpegExe.Sha256)
           && Sha256Equals(Path.Combine(root, "ffprobe.exe"), pins.FfprobeExe.Sha256);

    private static string? FindGalleryElsewhere(ToolPinTable pins, IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var gallery = Path.Combine(root, "gallery-dl.exe");
            if (Sha256Equals(gallery, pins.GalleryDlExe.Sha256))
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
                return await EnsureCoreAsync(Pins, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
                when (ex.Message.StartsWith("Hash mismatch", StringComparison.Ordinal)
                      && !ReferenceEquals(Pins, ToolPins.Embedded))
            {
                // A remote pin that does not match its download must not brick the app:
                // fall back to the pins compiled into this build.
                AppLog.Warn($"Remote tool pins rejected: {ex.Message}. Using embedded pins.");
                ToolPins.ResetToEmbedded();
                return await EnsureCoreAsync(Pins, progress, cancellationToken).ConfigureAwait(false);
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
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var dest = LocalToolsDirectory;
        Directory.CreateDirectory(dest);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };

        progress?.Report("tools");

        // Only the local tools directory is written. A root beside the exe that
        // fails its hashes is left alone and simply not used.
        if (!HasVerifiedCore(dest, pins) && TryFind(pins, CandidateRoots()) is null)
        {
            await DownloadVerifiedAsync(http, pins.YtDlp.Url, Path.Combine(dest, "yt-dlp.exe"), pins.YtDlp.Sha256, cancellationToken)
                .ConfigureAwait(false);

            if (!HasVerifiedFfmpeg(dest, pins))
            {
                var zipPath = Path.Combine(dest, "ffmpeg.zip");
                await DownloadVerifiedAsync(http, pins.FfmpegZip.Url, zipPath, pins.FfmpegZip.Sha256, cancellationToken)
                    .ConfigureAwait(false);
                ExtractFfmpeg(zipPath, dest);
                TryDelete(zipPath);
                if (!HasVerifiedFfmpeg(dest, pins))
                {
                    TryDelete(Path.Combine(dest, "ffmpeg.exe"));
                    TryDelete(Path.Combine(dest, "ffprobe.exe"));
                    throw new InvalidOperationException("Hash mismatch for ffmpeg.exe.");
                }
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

        return TryFind(pins, CandidateRoots()) ?? throw new InvalidOperationException("Could not install yt-dlp or ffmpeg.");
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

internal sealed class ToolHash
{
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

internal sealed class ToolPinTable
{
    internal const string ResourceName = "FetchIt.tool-pins.json";

    /// <summary>Monotonic; a remote table never replaces a newer cached or embedded one.</summary>
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("ytDlp")]
    public required ToolPin YtDlp { get; init; }

    [JsonPropertyName("ffmpegZip")]
    public required ToolPin FfmpegZip { get; init; }

    /// <summary>Hash of ffmpeg.exe as extracted from <see cref="FfmpegZip"/>.</summary>
    [JsonPropertyName("ffmpegExe")]
    public required ToolHash FfmpegExe { get; init; }

    /// <summary>Hash of ffprobe.exe as extracted from <see cref="FfmpegZip"/>.</summary>
    [JsonPropertyName("ffprobeExe")]
    public required ToolHash FfprobeExe { get; init; }

    [JsonPropertyName("galleryDlExe")]
    public required ToolPin GalleryDlExe { get; init; }

    internal IEnumerable<ToolPin> Downloads => [YtDlp, FfmpegZip, GalleryDlExe];

    internal IEnumerable<string> AllHashes =>
        [YtDlp.Sha256, FfmpegZip.Sha256, FfmpegExe.Sha256, FfprobeExe.Sha256, GalleryDlExe.Sha256];

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
        if (YtDlp is null || FfmpegZip is null || FfmpegExe is null || FfprobeExe is null || GalleryDlExe is null)
            throw new InvalidOperationException("tool-pins.json is missing a pin.");
        if (Version < 1)
            throw new InvalidOperationException("tool-pins.json version must be a positive integer.");
        foreach (var hash in AllHashes)
        {
            if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidOperationException("tool-pins.json has a malformed sha256.");
        }

        foreach (var pin in Downloads)
        {
            if (!ToolPins.IsAllowedToolUrl(pin.Url))
                throw new InvalidOperationException($"tool-pins.json URL not allowed: {pin.Url}");
        }
    }

    internal bool SameAs(ToolPinTable other)
        => Version == other.Version
           && Downloads.Zip(other.Downloads).All(pair => pair.First.Url == pair.Second.Url)
           && AllHashes.Zip(other.AllHashes).All(pair =>
               pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase));
}

internal enum PinDecision
{
    /// <summary>Identical to the current table.</summary>
    Same,
    /// <summary>Strictly newer: adopt it.</summary>
    Newer,
    /// <summary>Different content but not a higher version: refuse (downgrade or replay).</summary>
    Stale
}

/// <summary>
/// Tool pins come from the embedded table, optionally overridden by a newer
/// copy of scripts/tool-pins.json fetched from this repository. The remote copy
/// is accepted only with a valid ECDSA P-256 signature from the project's
/// signing key and a version higher than the one in use, so a bare write to
/// the branch cannot change which binaries the app runs.
/// </summary>
internal static class ToolPins
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/stuckinowhere/fetch-it/main/scripts/tool-pins.json";
    public const string RemoteSignatureUrl = RemoteUrl + ".sig";
    internal const string PublicKeyResourceName = "FetchIt.tool-pins.pub";

    /// <summary>Only these release paths may host a pinned binary.</summary>
    internal static readonly string[] AllowedUrlPrefixes =
    [
        "https://github.com/yt-dlp/yt-dlp/releases/download/",
        "https://github.com/GyanD/codexffmpeg/releases/download/",
        "https://codeberg.org/mikf/gallery-dl/releases/download/"
    ];

    internal static readonly string PublicKeyBase64 = LoadPublicKey();
    internal static readonly ToolPinTable Embedded = ToolPinTable.Load();
    private static volatile ToolPinTable _current = LoadCachedOrEmbedded();

    public static ToolPinTable Current => _current;

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "tool-pins.json");

    public static string CacheSignaturePath => CachePath + ".sig";

    internal static bool IsAllowedToolUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        // AbsoluteUri is normalized (lowercase host, dot segments resolved), so a
        // prefix match cannot be escaped with "..", "@" or mixed case.
        var normalized = uri.AbsoluteUri;
        return AllowedUrlPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal))
               && !normalized.Contains("/../", StringComparison.Ordinal);
    }

    private static string LoadPublicKey()
    {
        using var stream = typeof(ToolBootstrapper).Assembly.GetManifestResourceStream(PublicKeyResourceName)
            ?? throw new InvalidOperationException($"Missing embedded {PublicKeyResourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    /// <summary>ECDSA P-256 over SHA-256, IEEE P1363 (r||s) signature, SPKI public key.</summary>
    internal static bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKeyBase64)
    {
        try
        {
            if (signature.Length != 64)
                return false;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    internal static bool VerifySignature(byte[] data, string signatureBase64)
    {
        try
        {
            return VerifySignature(data, Convert.FromBase64String(signatureBase64.Trim()), PublicKeyBase64);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static PinDecision Decide(ToolPinTable current, ToolPinTable candidate)
    {
        if (candidate.SameAs(current))
            return PinDecision.Same;
        return candidate.Version > current.Version ? PinDecision.Newer : PinDecision.Stale;
    }

    private static ToolPinTable LoadCachedOrEmbedded()
    {
        try
        {
            if (File.Exists(CachePath) && File.Exists(CacheSignaturePath))
            {
                var bytes = File.ReadAllBytes(CachePath);
                if (!VerifySignature(bytes, File.ReadAllText(CacheSignaturePath)))
                    throw new InvalidOperationException("cached signature invalid");
                var cached = ToolPinTable.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                if (Decide(Embedded, cached) == PinDecision.Newer)
                    return cached;
            }
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
        TryDelete(CachePath);
        TryDelete(CacheSignaturePath);
    }

    /// <summary>Fetch the latest signed pins; never throws. Returns true when pins changed.</summary>
    public static async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 64 * 1024 };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WasdFetchIt-Pins");
            var json = await http.GetByteArrayAsync(RemoteUrl, cancellationToken).ConfigureAwait(false);
            var sig = await http.GetStringAsync(RemoteSignatureUrl, cancellationToken).ConfigureAwait(false);
            return Apply(json, sig);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Tool pin refresh skipped: {ex.Message}");
            return false;
        }
    }

    internal static bool Apply(byte[] json, string signatureBase64)
    {
        if (json.Length > 64 * 1024)
            throw new InvalidOperationException("tool-pins.json is too large.");
        if (!VerifySignature(json, signatureBase64))
            throw new InvalidOperationException("tool-pins.json signature does not verify.");

        var table = ToolPinTable.Parse(System.Text.Encoding.UTF8.GetString(json));
        switch (Decide(_current, table))
        {
            case PinDecision.Same:
                return false;
            case PinDecision.Stale:
                throw new InvalidOperationException(
                    $"tool-pins.json version {table.Version} is not newer than {_current.Version}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllBytes(CachePath, json);
        File.WriteAllText(CacheSignaturePath, signatureBase64.Trim());
        _current = table;
        AppLog.Info($"Tool pins updated from remote (version {table.Version}).");
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
