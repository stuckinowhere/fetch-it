using System.IO.Compression;
using System.Security.Cryptography;

namespace FetchIt.Services;

public sealed class ToolPaths
{
    public required string YtDlp { get; init; }
    public required string FfmpegDir { get; init; }
    public string? GalleryDl { get; init; }
    public IReadOnlyList<string> GalleryPrefix { get; init; } = [];
    public IReadOnlyDictionary<string, string>? GalleryEnvironment { get; init; }

    public IReadOnlyList<string> GalleryArguments(IEnumerable<string> rest)
    {
        var args = new List<string>(GalleryPrefix);
        args.AddRange(rest);
        return args;
    }
}

public static class ToolBootstrapper
{
    public const string YtDlpUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe";
    public const string YtDlpSha256 =
        "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a";

    public const string FfmpegZipUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-09-14-51/ffmpeg-N-126482-g903325e279-win64-gpl.zip";
    public const string FfmpegZipSha256 =
        "6c60a0c17a02eab0ead59c4597e58268fa024b45ff08cd27b6d6be8e50fd2588";

    public const string GalleryDlWheelUrl =
        "https://files.pythonhosted.org/packages/d6/6b/ac77fe9f7c050ca04de17174f5fc384ae104b009424b147089f0a8037272/gallery_dl-1.32.11-py3-none-any.whl";
    public const string GalleryDlWheelSha256 =
        "67fcb941083defebcf0d075e6c0c0aab84a5d8ef23e927f34bf9b9860754958b";

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
            if (!File.Exists(yt) || !Sha256Equals(yt, YtDlpSha256))
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
            await DownloadVerifiedAsync(http, YtDlpUrl, Path.Combine(dest, "yt-dlp.exe"), YtDlpSha256, cancellationToken)
                .ConfigureAwait(false);

            var zipPath = Path.Combine(dest, "ffmpeg.zip");
            await DownloadVerifiedAsync(http, FfmpegZipUrl, zipPath, FfmpegZipSha256, cancellationToken)
                .ConfigureAwait(false);
            ExtractFfmpeg(zipPath, dest);
            TryDelete(zipPath);
        }

        var wheel = Path.Combine(dest, "gallery-dl.whl");
        try
        {
            await DownloadVerifiedAsync(http, GalleryDlWheelUrl, wheel, GalleryDlWheelSha256, cancellationToken)
                .ConfigureAwait(false);
            ExtractWheel(wheel, Path.Combine(dest, "gallery-dl-lib"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // Instagram falls back to yt-dlp if Python or the wheel is missing.
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
