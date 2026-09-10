using FetchIt.Models;

namespace FetchIt.Services;

public sealed class YtDlpService
{
    public async Task<MediaProbe> ProbeAsync(
        string url,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new FetchProgress { Status = "Getting tools…" });
        var tools = await ToolBootstrapper.EnsureAsync(new Progress<string>(_ =>
        {
            progress?.Report(new FetchProgress { Status = "Getting tools…" });
        }), cancellationToken).ConfigureAwait(false);
        progress?.Report(new FetchProgress { Status = "Reading link…" });
        var args = new List<string>
        {
            "--dump-single-json",
            "--no-download",
            "--no-warnings",
            "--ignore-no-formats-error",
            "--socket-timeout", "45",
            "--ffmpeg-location", tools.FfmpegDir
        };
        args.Add(url);

        var text = await ProcessRunner.RunTextAsync(tools.YtDlp, args, cancellationToken).ConfigureAwait(false);
        if (LooksLikeFailure(text))
            throw new InvalidOperationException(ShortError(text));
        return YtDlpParser.Parse(text);
    }

    public async Task DownloadAsync(
        string url,
        string folder,
        string title,
        int fileCount,
        DuplicateChoice duplicate,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var tools = await ToolBootstrapper.EnsureAsync(new Progress<string>(_ =>
        {
            progress.Report(new FetchProgress { Status = "tools" });
        }), cancellationToken).ConfigureAwait(false);

        var dest = Path.GetFullPath(folder);
        if (!FolderStore.CanWrite(dest))
            throw new InvalidOperationException("Windows blocked that folder. Pick another save folder.");

        var stem = MediaRouter.SanitizeFolderName(title);
        var output = Path.Combine(dest, $"{stem}.%(ext)s");
        if (duplicate == DuplicateChoice.KeepBoth)
            output = SaveClash.UniquePath(dest, $"{stem}.mp4").Replace(".mp4", ".%(ext)s", StringComparison.OrdinalIgnoreCase);

        var args = new List<string>
        {
            "--newline",
            "--progress",
            "--no-warnings",
            "--socket-timeout", "45",
            "--ffmpeg-location", tools.FfmpegDir,
            "--windows-filenames",
            "--merge-output-format", "mp4",
            "-f", "bv*+ba/b",
            "-o", output
        };
        if (duplicate == DuplicateChoice.Overwrite)
            args.Add("--force-overwrites");
        else
            args.Add("--no-overwrites");
        args.Add(url);

        var code = await ProcessRunner.RunAsync(tools.YtDlp, args, line =>
        {
            var parsed = YtDlpParser.TryParseDownloadProgress(line);
            if (parsed is null)
                return;
            progress.Report(new FetchProgress
            {
                Percent = parsed.Value.Percent ?? 0,
                HasPercent = parsed.Value.Percent is not null,
                Status = parsed.Value.Status
            });
        }, cancellationToken).ConfigureAwait(false);

        if (code != 0)
            throw new InvalidOperationException("Could not fetch that video.");
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
        if (MediaRouter.LooksPrivate(text))
            return MediaRouter.PublicOnlyMessage;
        if (text.Contains("logged-in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cookies-from-browser", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Sign in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("login required", StringComparison.OrdinalIgnoreCase))
            return "That site wants a signed-in session. Close Chrome, then paste the link again.";
        return "Could not read that link.";
    }
}
