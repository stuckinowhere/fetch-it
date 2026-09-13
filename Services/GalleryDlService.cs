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
            throw new InvalidOperationException(ShortError(url, ""));

        var social = MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsSocialPostHost(uri);
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
        if (string.IsNullOrWhiteSpace(text) || LooksLikeFailure(text))
            throw new InvalidOperationException(ShortError(url, text));
        var probe = GalleryDlParser.Parse(text);
        if (probe.FileCount > 0 && probe.Items.Count > 0)
            return probe;
        throw new InvalidOperationException(ShortError(url, text));
    }

    public async Task DownloadAsync(
        string url,
        string folder,
        MediaProbe probe,
        DuplicateChoice duplicate,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var dest = Path.GetFullPath(folder);
        if (!FolderStore.CanWrite(dest))
            throw new InvalidOperationException("Windows blocked that folder. Pick another save folder.");

        var direct = probe.Items
            .Select(item => item.DownloadUrl)
            .Where(link => MediaRouter.TryParseHttpUrl(link, out _))
            .Cast<string>()
            .ToList();
        if (direct.Count == probe.Items.Count && direct.Count > 0)
        {
            await DownloadDirectAsync(dest, probe, direct, duplicate, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DownloadWithToolAsync(url, dest, probe.FileCount, duplicate, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadDirectAsync(
        string dest,
        MediaProbe probe,
        IReadOnlyList<string> urls,
        DuplicateChoice duplicate,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        var stem = MediaRouter.SanitizeFolderName(probe.Title);
        var done = 0;
        foreach (var mediaUrl in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(new Uri(mediaUrl).AbsolutePath).Trim('.');
            if (string.IsNullOrWhiteSpace(ext))
                ext = "jpg";
            var fileName = $"{stem}_{done + 1}.{ext}";
            var name = duplicate == DuplicateChoice.KeepBoth
                ? SaveClash.UniquePath(dest, fileName)
                : Path.Combine(dest, fileName);
            using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            if (NeedsXReferer(mediaUrl))
                request.Headers.Referrer = new Uri("https://x.com/");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var tmp = name + ".part";
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(tmp))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            File.Move(tmp, name, overwrite: true);
            done++;
            progress.Report(new FetchProgress
            {
                Percent = 100.0 * done / urls.Count,
                HasPercent = true,
                Status = $"{done} / {urls.Count}"
            });
        }

        if (done == 0)
            throw new InvalidOperationException("Could not save those files.");
    }

    private async Task DownloadWithToolAsync(
        string url,
        string dest,
        int fileCount,
        DuplicateChoice duplicate,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var tools = await ToolBootstrapper.EnsureAsync(new Progress<string>(_ =>
        {
            progress.Report(new FetchProgress { Status = "tools" });
        }), cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(tools.GalleryDl))
            throw new InvalidOperationException(ShortError(url, ""));

        var done = 0;
        var social = MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsSocialPostHost(uri);
        using var cookies = SessionCookies.BindForTool(social);
        var args = new List<string>(tools.GalleryPrefix)
        {
            "-D", dest,
            "--no-mtime",
            "-o", "path-extended=false",
            "-o", duplicate == DuplicateChoice.Overwrite ? "skip=false" : "skip=true"
        };
        if (duplicate == DuplicateChoice.KeepBoth)
        {
            var tag = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            args.Add("-o");
            args.Add($"filename={{filename}}_{tag}.{{extension}}");
        }
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
            throw new InvalidOperationException("Could not save those files.");
    }

    private static bool NeedsXReferer(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host.Trim().ToLowerInvariant();
        if (host.StartsWith("www."))
            host = host[4..];
        return host is "pbs.twimg.com" or "video.twimg.com" or "twimg.com"
            or "x.com" or "twitter.com";
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

    private static string ShortError(string url, string text)
    {
        if (MediaRouter.LooksPrivate(text))
            return MediaRouter.PublicOnlyMessage;
        var social = MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsSocialPostHost(uri);
        if (social)
            return MediaRouter.InstagramSessionMessage;
        return "Could not read that link.";
    }
}
