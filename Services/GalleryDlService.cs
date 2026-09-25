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
        AddHostOptions(args, url);
        args.AddRange(cookies.Arguments);
        args.Add(url);

        var text = await ProcessRunner.RunTextAsync(
            tools.GalleryDl, args, cancellationToken, tools.GalleryEnvironment, TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
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

        var jobs = PlanDirectFiles(dest, probe, duplicate);
        if (jobs.Count == probe.Items.Count && jobs.Count > 0)
        {
            await DownloadDirectAsync(jobs, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DownloadWithToolAsync(url, dest, probe.FileCount, duplicate, progress, cancellationToken).ConfigureAwait(false);
    }

    internal const int DirectParallel = 4;

    internal static IReadOnlyList<(string Url, string Path)> PlanDirectFiles(
        string dest,
        MediaProbe probe,
        DuplicateChoice duplicate)
    {
        var planned = SaveClash.PlannedNames(probe);
        var jobs = new List<(string, string)>();
        for (var i = 0; i < probe.Items.Count; i++)
        {
            var raw = probe.Items[i].DownloadUrl;
            if (raw is null || !MediaRouter.TryParseHttpUrl(raw, out _))
                continue;

            var fileName = i < planned.Count
                ? planned[i]
                : $"{MediaRouter.SanitizeFolderName(probe.Title)}_{i + 1}.jpg";
            var path = duplicate == DuplicateChoice.KeepBoth
                ? SaveClash.UniquePath(dest, fileName)
                : Path.Combine(dest, fileName);
            jobs.Add((ThumbnailUrl.ForSave(raw), path));
        }

        return jobs;
    }

    internal static Task DownloadDirectAsync(
        IReadOnlyList<(string Url, string Path)> jobs,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
        => DownloadDirectAsync(jobs, progress, cancellationToken, http: null);

    internal static async Task DownloadDirectAsync(
        IReadOnlyList<(string Url, string Path)> jobs,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken,
        HttpClient? http,
        int maxParallel = DirectParallel)
    {
        var owns = http is null;
        SocketsHttpHandler? handler = null;
        if (owns)
        {
            handler = HttpFetch.CreateHandler(maxConnectionsPerServer: DirectParallel);
            http = HttpFetch.CreateClient(handler, Timeout.InfiniteTimeSpan);
        }

        var client = http!;
        var leftover = jobs.ToList();
        var total = jobs.Count;
        var done = 0;

        async Task AttemptAsync(IReadOnlyList<(string Url, string Path)> batch, int parallel)
        {
            var failed = new System.Collections.Concurrent.ConcurrentBag<(string Url, string Path)>();
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallel,
                    CancellationToken = cancellationToken
                },
                async (job, token) =>
                {
                    try
                    {
                        if (total == 1)
                        {
                            await HttpFetch.SaveStreamAsync(client, job.Url, job.Path, token, progress)
                                .ConfigureAwait(false);
                            Interlocked.Increment(ref done);
                        }
                        else
                        {
                            await HttpFetch.SaveStreamAsync(client, job.Url, job.Path, token)
                                .ConfigureAwait(false);
                            var n = Interlocked.Increment(ref done);
                            progress.Report(new FetchProgress
                            {
                                Percent = 100.0 * n / total,
                                HasPercent = true,
                                Status = $"{n} / {total}"
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        failed.Add(job);
                    }
                }).ConfigureAwait(false);
            leftover = failed.ToList();
        }

        try
        {
            var firstParallel = total <= 1 ? 1 : Math.Min(Math.Max(1, maxParallel), total);
            await AttemptAsync(leftover, firstParallel).ConfigureAwait(false);
            if (leftover.Count > 0)
                await AttemptAsync(leftover, 1).ConfigureAwait(false);

            if (done == 0)
                throw new InvalidOperationException("Could not save those files.");
            if (leftover.Count > 0)
                throw new InvalidOperationException($"Saved {done} of {total} files.");
        }
        finally
        {
            if (owns)
            {
                client.Dispose();
                handler?.Dispose();
            }
        }
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
        AddHostOptions(args, url);
        args.AddRange(cookies.Arguments);
        args.Add(url);

        var code = await ProcessRunner.RunAsync(tools.GalleryDl, args, line =>
        {
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

        EnsureToolSucceeded(code, done, fileCount);
    }

    internal static void EnsureToolSucceeded(int code, int done, int fileCount)
    {
        if (code == 0)
            return;
        if (done <= 0)
            throw new InvalidOperationException("Could not save those files.");
        var total = Math.Max(fileCount, done);
        throw new InvalidOperationException($"Saved {done} of {total} files.");
    }

    internal static bool LooksLikeSavedFile(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;
        if (trimmed.Contains("error", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("warning", StringComparison.OrdinalIgnoreCase))
            return false;
        // [instagram][info] Visiting https://... is a log line, not a save.
        if (trimmed.StartsWith('[') && trimmed.Contains("][", StringComparison.Ordinal))
            return false;
        return trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.StartsWith('#');
    }

    private static bool LooksLikeFailure(string text)
    {
        var trimmed = text.TrimStart();
        return !trimmed.Contains('{') && !trimmed.Contains('[');
    }

    internal static void AddHostOptions(List<string> args, string url)
    {
        if (!MediaRouter.TryParseHttpUrl(url, out var uri))
            return;
        if (MediaRouter.IsBunkr(uri))
            args.AddRange(["-o", "extractor.bunkr.tlds=true"]);
        if (!MediaRouter.IsGofile(uri))
            return;
        args.AddRange(["-o", "extractor.gofile.salt=" + GofileService.WebsiteSalts[0]]);
        var token = GofileService.TryReadSavedToken();
        if (!string.IsNullOrEmpty(token))
            args.AddRange(["-o", "extractor.gofile.api-token=" + token]);
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
