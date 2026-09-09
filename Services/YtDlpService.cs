using FetchIt.Models;

namespace FetchIt.Services;

public sealed class YtDlpService
{
    public async Task<MediaProbe> ProbeAsync(string url, bool useCookies, CancellationToken cancellationToken)
    {
        var tools = await ToolBootstrapper.EnsureAsync(null, cancellationToken).ConfigureAwait(false);
        var args = new List<string>
        {
            "--dump-single-json",
            "--no-download",
            "--no-warnings",
            "--no-playlist",
            "--ignore-no-formats-error",
            "--ffmpeg-location", tools.FfmpegDir
        };
        AddCookies(args, useCookies);
        args.Add(url);

        var text = await ProcessRunner.RunTextAsync(tools.YtDlp, args, cancellationToken).ConfigureAwait(false);
        if (LooksLikeFailure(text))
            throw new InvalidOperationException(ShortError(text));
        return YtDlpParser.Parse(text, needsLogin: useCookies);
    }

    public async Task DownloadAsync(
        string url,
        string folder,
        string title,
        VideoQuality quality,
        bool useCookies,
        int fileCount,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var tools = await ToolBootstrapper.EnsureAsync(new Progress<string>(_ =>
        {
            progress.Report(new FetchProgress { Status = "tools" });
        }), cancellationToken).ConfigureAwait(false);

        var dest = fileCount > 1
            ? Path.Combine(folder, MediaRouter.SanitizeFolderName(title))
            : folder;
        Directory.CreateDirectory(dest);

        var args = new List<string>
        {
            "--newline",
            "--no-warnings",
            "--ffmpeg-location", tools.FfmpegDir,
            "-o", Path.Combine(dest, "%(title)s.%(ext)s")
        };
        args.AddRange(FormatArgs(quality));
        AddCookies(args, useCookies);
        args.Add(url);

        var code = await ProcessRunner.RunAsync(tools.YtDlp, args, line =>
        {
            var percent = YtDlpParser.TryParsePercent(line);
            var status = YtDlpParser.TryParseSizeStatus(line) ?? "";
            if (percent is not null || status.Length > 0)
            {
                progress.Report(new FetchProgress
                {
                    Percent = percent ?? 0,
                    Status = status
                });
            }
        }, cancellationToken).ConfigureAwait(false);

        if (code != 0)
            throw new InvalidOperationException("Could not fetch that video.");
    }

    public static IReadOnlyList<string> FormatArgs(VideoQuality quality) => quality switch
    {
        VideoQuality.P1080 => ["-f", "bestvideo[height<=1080]+bestaudio/best[height<=1080]/best"],
        VideoQuality.P720 => ["-f", "bestvideo[height<=720]+bestaudio/best[height<=720]/best"],
        VideoQuality.Audio => ["-x", "--audio-format", "m4a"],
        _ => ["-f", "bv*+ba/b"]
    };

    internal static void AddCookies(List<string> args, bool useCookies)
    {
        if (!useCookies)
            return;
        args.Add("--cookies-from-browser");
        args.Add("chrome");
    }

    private static bool LooksLikeFailure(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var trimmed = text.TrimStart();
        return !trimmed.Contains('{');
    }

    private static string ShortError(string text)
    {
        if (text.Contains("DRM", StringComparison.OrdinalIgnoreCase))
            return "That stream is protected.";
        if (text.Contains("login", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Private", StringComparison.OrdinalIgnoreCase))
            return "Needs login.";
        return "Could not read that link.";
    }
}
