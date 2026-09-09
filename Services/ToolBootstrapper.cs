using System.IO.Compression;

namespace FetchIt.Services;

public sealed class ToolPaths
{
    public required string YtDlp { get; init; }
    public required string GalleryDl { get; init; }
    public required string FfmpegDir { get; init; }
}

public static class ToolBootstrapper
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string GalleryDlUrl = "https://github.com/mikf/gallery-dl/releases/latest/download/gallery-dl.exe";
    public const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static string LocalToolsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "tools");

    public static IEnumerable<string> CandidateRoots()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "tools");
        yield return baseDir;
        yield return LocalToolsDirectory;
    }

    public static ToolPaths? TryFind()
    {
        foreach (var root in CandidateRoots())
        {
            var yt = Path.Combine(root, "yt-dlp.exe");
            var gal = Path.Combine(root, "gallery-dl.exe");
            if (!File.Exists(yt) || !File.Exists(gal))
                continue;

            var ffmpegDir = File.Exists(Path.Combine(root, "ffmpeg.exe")) ? root : null;
            if (ffmpegDir is null)
            {
                var nested = Directory.Exists(root)
                    ? Directory.GetFiles(root, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                if (nested is not null)
                    ffmpegDir = Path.GetDirectoryName(nested);
            }

            if (ffmpegDir is null)
                continue;

            return new ToolPaths
            {
                YtDlp = yt,
                GalleryDl = gal,
                FfmpegDir = ffmpegDir
            };
        }

        return null;
    }

    public static async Task<ToolPaths> EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var existing = TryFind();
        if (existing is not null)
            return existing;

        var dest = LocalToolsDirectory;
        Directory.CreateDirectory(dest);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };

        progress?.Report("tools");
        await DownloadAsync(http, YtDlpUrl, Path.Combine(dest, "yt-dlp.exe"), cancellationToken).ConfigureAwait(false);
        await DownloadAsync(http, GalleryDlUrl, Path.Combine(dest, "gallery-dl.exe"), cancellationToken).ConfigureAwait(false);

        var zipPath = Path.Combine(dest, "ffmpeg.zip");
        await DownloadAsync(http, FfmpegZipUrl, zipPath, cancellationToken).ConfigureAwait(false);
        ExtractFfmpeg(zipPath, dest);
        TryDelete(zipPath);

        return TryFind() ?? throw new InvalidOperationException("Could not install yt-dlp, gallery-dl, or ffmpeg.");
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

    private static async Task DownloadAsync(HttpClient http, string url, string dest, CancellationToken cancellationToken)
    {
        if (File.Exists(dest) && new FileInfo(dest).Length > 1024)
            return;

        var tmp = dest + ".part";
        await using (var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
        await using (var file = File.Create(tmp))
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

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
