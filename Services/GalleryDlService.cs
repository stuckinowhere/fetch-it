using FetchIt.Models;

namespace FetchIt.Services;

public sealed class GalleryDlService
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
        if (string.IsNullOrEmpty(tools.GalleryDl))
            throw new InvalidOperationException(ShortError(""));

        var social = MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsGalleryHost(uri);
        using var cookies = SessionCookies.BindForTool(social);
        var args = new List<string>(tools.GalleryPrefix)
        {
            "--dump-json",
            "--no-download",
            "--range", "1-50",
            "-o", "extractor.instagram.sleep-request=0"
        };
        args.AddRange(cookies.Arguments);
        args.Add(url);

        var text = await ProcessRunner.RunTextAsync(
            tools.GalleryDl, args, cancellationToken, tools.GalleryEnvironment).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text) || LooksLikeFailure(text) || MediaRouter.LooksPrivate(text)
            || MediaRouter.LooksLikeMissingSession(text))
            throw new InvalidOperationException(ShortError(text));
        var probe = GalleryDlParser.Parse(text);
        if (probe.FileCount == 0 || probe.Items.Count == 0)
            throw new InvalidOperationException(ShortError(text));
        return probe;
    }

    public async Task DownloadAsync(
        string url,
        string folder,
        string title,
        int fileCount,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var tools = await ToolBootstrapper.EnsureAsync(new Progress<string>(_ =>
        {
            progress.Report(new FetchProgress { Status = "tools" });
        }), cancellationToken).ConfigureAwait(false);

        var dest = fileCount > 1
            ? MediaRouter.SafeCombine(folder, title)
            : Path.GetFullPath(folder);
        Directory.CreateDirectory(dest);

        if (string.IsNullOrEmpty(tools.GalleryDl))
            throw new InvalidOperationException(MediaRouter.InstagramSessionMessage);

        var done = 0;
        var social = MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsGalleryHost(uri);
        using var cookies = SessionCookies.BindForTool(social);
        var args = new List<string>(tools.GalleryPrefix)
        {
            "-D", dest,
            "--no-mtime"
        };
        args.AddRange(cookies.Arguments);
        args.Add(url);

        var code = await ProcessRunner.RunAsync(tools.GalleryDl, args, line =>
        {
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                return;
            if (LooksLikeSavedFile(line))
            {
                done++;
                var total = Math.Max(fileCount, done);
                progress.Report(new FetchProgress
                {
                    Percent = total == 0 ? 0 : 100.0 * done / total,
                    HasPercent = true,
                    Status = $"{done} / {total}"
                });
            }
        }, cancellationToken, environment: tools.GalleryEnvironment).ConfigureAwait(false);

        if (code != 0 && done == 0)
            throw new InvalidOperationException(MediaRouter.InstagramSessionMessage);
    }

    internal static bool LooksLikeSavedFile(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;
        return trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.StartsWith('#');
    }

    private static bool LooksLikeFailure(string text)
    {
        var trimmed = text.TrimStart();
        return !trimmed.Contains('{') && !trimmed.Contains('[');
    }

    private static string ShortError(string text)
    {
        if (MediaRouter.LooksPrivate(text))
            return MediaRouter.PublicOnlyMessage;
        if (MediaRouter.LooksLikeMissingSession(text) || string.IsNullOrWhiteSpace(text))
            return MediaRouter.InstagramSessionMessage;
        return MediaRouter.InstagramSessionMessage;
    }
}
