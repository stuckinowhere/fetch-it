using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FetchIt.Services;

public sealed class ToolPaths
{
    public required string YtDlp { get; init; }
    public required string FfmpegDir { get; init; }
    public string? GalleryDl { get; init; }
    public IReadOnlyList<string> GalleryPrefix { get; init; } = [];
    public IReadOnlyDictionary<string, string>? GalleryEnvironment { get; init; }
}

public static class ToolBootstrapper
{
    internal static readonly ToolPinTable Pins = ToolPinTable.Load();

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

    public static ToolPaths? TryFind()
    {
        foreach (var root in CandidateRoots())
        {
            var yt = Path.Combine(root, "yt-dlp.exe");
            if (!File.Exists(yt) || !Sha256Equals(yt, Pins.YtDlp.Sha256))
                continue;

            var ffmpeg = Path.Combine(root, "ffmpeg.exe");
            if (!IsUsableFfmpeg(ffmpeg))
                continue;

            var galleryLib = Path.Combine(root, "gallery-dl-lib");
            string? python = null;
            IReadOnlyList<string> prefix = [];
            IReadOnlyDictionary<string, string>? env = null;
            if (Directory.Exists(Path.Combine(galleryLib, "gallery_dl")))
                python = FindPython();
            if (python is not null)
            {
                prefix = ["-m", "gallery_dl"];
                env = new Dictionary<string, string> { ["PYTHONPATH"] = galleryLib };
            }

            return new ToolPaths
            {
                YtDlp = yt,
                FfmpegDir = root,
                GalleryDl = python,
                GalleryPrefix = prefix,
                GalleryEnvironment = env
            };
        }

        return null;
    }

    public static async Task<ToolPaths> EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var existing = TryFind();
        if (existing is not null && existing.GalleryDl is not null)
            return existing;

        var dest = LocalToolsDirectory;
        Directory.CreateDirectory(dest);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };

        progress?.Report("tools");
        if (existing is null)
        {
            await DownloadVerifiedAsync(http, Pins.YtDlp.Url, Path.Combine(dest, "yt-dlp.exe"), Pins.YtDlp.Sha256, cancellationToken)
                .ConfigureAwait(false);

            var zipPath = Path.Combine(dest, "ffmpeg.zip");
            await DownloadVerifiedAsync(http, Pins.FfmpegZip.Url, zipPath, Pins.FfmpegZip.Sha256, cancellationToken)
                .ConfigureAwait(false);
            ExtractFfmpeg(zipPath, dest);
            TryDelete(zipPath);
        }

        var wheel = Path.Combine(dest, "gallery-dl.whl");
        try
        {
            await DownloadVerifiedAsync(http, Pins.GalleryDlWheel.Url, wheel, Pins.GalleryDlWheel.Sha256, cancellationToken)
                .ConfigureAwait(false);
            ExtractWheel(wheel, Path.Combine(dest, "gallery-dl-lib"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
        }

        return TryFind() ?? existing ?? throw new InvalidOperationException("Could not install yt-dlp or ffmpeg.");
    }

    internal static bool Sha256Equals(string path, string expectedHex)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return hash.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string? FindPython()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.Combine(dir.Trim(), "python.exe");
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 4096)
                return candidate;
        }

        return null;
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

    internal static void ExtractWheel(string wheelPath, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(wheelPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(dest, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
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

    [JsonPropertyName("galleryDlWheel")]
    public required ToolPin GalleryDlWheel { get; init; }

    internal static ToolPinTable Load()
    {
        using var stream = typeof(ToolBootstrapper).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded {ResourceName}.");
        var table = JsonSerializer.Deserialize<ToolPinTable>(stream)
            ?? throw new InvalidOperationException("Invalid scripts/tool-pins.json.");
        if (table.YtDlp is null || table.FfmpegZip is null || table.GalleryDlWheel is null)
            throw new InvalidOperationException("scripts/tool-pins.json is missing a pin.");
        foreach (var pin in new[] { table.YtDlp, table.FfmpegZip, table.GalleryDlWheel })
        {
            if (string.IsNullOrWhiteSpace(pin.Url) || string.IsNullOrWhiteSpace(pin.Sha256))
                throw new InvalidOperationException("scripts/tool-pins.json is missing a url or sha256.");
        }

        return table;
    }
}
