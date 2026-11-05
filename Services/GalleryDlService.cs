using FetchIt.Models;

namespace FetchIt.Services;

public sealed class GalleryDlService
{
    public async Task<MediaProbe> ProbeAsync(
        string url,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken,
        bool wholeProfile = false)
    {
        progress?.Report(new FetchProgress { Status = "Getting tools…" });
        var tools = await ToolBootstrapper.EnsureAsync(new Progress<string>(_ =>
        {
            progress?.Report(new FetchProgress { Status = "Getting tools…" });
        }), cancellationToken).ConfigureAwait(false);
        progress?.Report(new FetchProgress { Status = wholeProfile ? "Reading the whole profile…" : "Reading link…" });
        if (string.IsNullOrEmpty(tools.GalleryDl))
            throw new InvalidOperationException(ShortError(url, ""));

        var social = MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsSocialPostHost(uri);
        using var cookies = SessionCookies.BindForTool(social);
        var args = new List<string>
        {
            // A profile is a list of tabs; -J follows them instead of naming them.
            wholeProfile ? "--resolve-json" : "--dump-json",
            "--no-download",
            "-o", "extractor.instagram.sleep-request=1.0-2.0"
        };
        if (wholeProfile)
            args.AddRange(["-o", "extractor.instagram.include=" + InstagramEverything]);
        AddHostOptions(args, url);
        args.AddRange(cookies.Arguments);
        args.Add(url);

        var text = await ProcessRunner.RunTextAsync(
            tools.GalleryDl, args, cancellationToken, timeout: ReadLimit)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text) || LooksLikeFailure(text))
        {
            AppLog.Warn($"gallery-dl probe failed for {AppLog.SafeUrl(url)}:\n{text}");
            throw new InvalidOperationException(ShortError(url, text));
        }
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
        CancellationToken cancellationToken,
        bool wholeProfile = false)
    {
        var dest = Path.GetFullPath(folder);
        if (!FolderStore.CanWrite(dest))
            throw new InvalidOperationException("Windows blocked that folder. Pick another save folder.");

        if (CanDownloadDirect(probe))
        {
            var jobs = PlanDirectFiles(dest, probe, duplicate);
            if (jobs.Count > 0)
                await DownloadDirectAsync(jobs, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DownloadWithToolAsync(url, dest, probe.FileCount, duplicate, wholeProfile, progress, cancellationToken).ConfigureAwait(false);
    }

    // Everything a signed-in account can see on a profile, as files. No "info":
    // that is metadata, not media.
    internal const string InstagramEverything = "avatar,posts,reels,stories,highlights,tagged";

    internal const int DirectParallel = 4;

    // Reading a large profile takes minutes; the Stop button is the user's limit.
    internal static readonly TimeSpan ReadLimit = TimeSpan.FromMinutes(30);

    // Fetch lists every file, so Download only needs a link for each of them.
    internal static bool CanDownloadDirect(MediaProbe probe)
        => probe.Items.Count > 0
           && probe.Items.All(item => MediaRouter.TryParseHttpUrl(item.DownloadUrl, out _));

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
            if (duplicate == DuplicateChoice.Skip && File.Exists(Path.Combine(dest, fileName)))
                continue;

            var path = duplicate == DuplicateChoice.KeepBoth
                ? SaveClash.UniquePath(dest, fileName)
                : Path.Combine(dest, fileName);
            jobs.Add((ThumbnailUrl.ForSave(raw), path));
        }

        return jobs;
    }

    internal static async Task DownloadDirectAsync(
        IReadOnlyList<(string Url, string Path)> jobs,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        using var handler = HttpFetch.CreateHandler(maxConnectionsPerServer: DirectParallel);
        using var http = HttpFetch.CreateClient(handler, Timeout.InfiniteTimeSpan);
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
                            await HttpFetch.SaveStreamAsync(http, job.Url, job.Path, token, progress)
                                .ConfigureAwait(false);
                            Interlocked.Increment(ref done);
                        }
                        else
                        {
                            await HttpFetch.SaveStreamAsync(http, job.Url, job.Path, token)
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

        var firstParallel = total <= 1 ? 1 : Math.Min(DirectParallel, total);
        await AttemptAsync(leftover, firstParallel).ConfigureAwait(false);
        if (leftover.Count > 0)
            await AttemptAsync(leftover, 1).ConfigureAwait(false);

        if (done == 0)
            throw new InvalidOperationException("Could not save those files.");
        if (leftover.Count > 0)
            throw new InvalidOperationException($"Saved {done} of {total} files.");
    }

    private async Task DownloadWithToolAsync(
        string url,
        string dest,
        int fileCount,
        DuplicateChoice duplicate,
        bool wholeProfile,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var tools = await ToolBootstrapper.EnsureAsync(new Progress<string>(_ =>
        {
            progress.Report(new FetchProgress { Status = "tools" });
        }), cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(tools.GalleryDl))
            throw new InvalidOperationException(ShortError(url, ""));

        // Instagram names are unique ids, so a repeat run only adds what is new
        // (today's stories, say) instead of a _2 copy of every photo.
        var moveAs = wholeProfile ? DuplicateChoice.Skip : duplicate;

        var done = 0;
        var social = MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsSocialPostHost(uri);
        using var cookies = SessionCookies.BindForTool(social);
        using var staging = ToolStaging.Create();
        var args = new List<string>
        {
            "-D", staging.Folder,
            "--no-mtime",
            "-o", "path-extended=false"
        };
        if (wholeProfile)
            args.AddRange(["-o", "extractor.instagram.include=" + InstagramEverything]);
        AddHostOptions(args, url);
        args.AddRange(cookies.Arguments);
        args.Add(url);

        int code;
        try
        {
            code = await ProcessRunner.RunAsync(tools.GalleryDl, args, line =>
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
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            staging.MoveTo(dest, moveAs); // Keep what finished before Stop.
            throw;
        }

        progress.Report(new FetchProgress { Status = "Saving…" });
        done = Math.Max(done, staging.MoveTo(dest, moveAs));
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
        return ReadError(social, SessionCookies.HasUsableFile(), text);
    }

    /// <summary>
    /// A saved session stays saved unless the tool says login is needed: a
    /// deleted post or a rate limit must not sign the user out.
    /// </summary>
    internal static string ReadError(bool social, bool hasSavedSession, string text)
    {
        if (social && (!hasSavedSession || LooksLikeLoginRequired(text)))
            return MediaRouter.InstagramSessionMessage;
        return "Could not read that link.";
    }

    internal static bool LooksLikeLoginRequired(string text)
        => LoginMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] LoginMarkers =
        ["accounts/login", "login page", "login required", "unauthorized", "challenge", "checkpoint"];
}
