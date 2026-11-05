using System.Text.Json;
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
        if (wholeProfile)
            return await ListProfileAsync(url, tools, progress, cancellationToken).ConfigureAwait(false);

        var social = MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsSocialPostHost(uri);
        using var cookies = SessionCookies.BindForTool(social);
        var args = new List<string>
        {
            "--dump-json",
            "--no-download",
            "-o", "extractor.instagram.sleep-request=1.0-2.0"
        };
        AddHostOptions(args, url);
        args.AddRange(cookies.Arguments);
        args.Add(url);

        // gallery-dl prints everything at the end, so show that it is alive.
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ticker = ShowElapsedAsync("Reading link…", progress, reading.Token);
        string text;
        try
        {
            text = await ProcessRunner.RunTextAsync(
                tools.GalleryDl, args, cancellationToken, timeout: ReadLimit)
                .ConfigureAwait(false);
        }
        finally
        {
            reading.Cancel();
            await ticker.ConfigureAwait(false);
        }

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

    /// <summary>
    /// A big profile can take hours, so gallery-dl writes each file to a list as
    /// it finds it. Whatever stops the run, what was found is kept.
    /// </summary>
    private static async Task<MediaProbe> ListProfileAsync(
        string url,
        ToolPaths tools,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var social = MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsSocialPostHost(uri);
        using var cookies = SessionCookies.BindForTool(social);
        using var staging = ToolStaging.Create();
        var args = new List<string>
        {
            "--no-download",
            "--no-skip",
            "-D", staging.Folder,
            "-o", "path-extended=false",
            "-o", "extractor.instagram.sleep-request=1.0-2.0",
            "-o", "extractor.instagram.include=" + InstagramEverything,
            "-o", ListingOption(staging.Folder)
        };
        AddHostOptions(args, url);
        args.AddRange(cookies.Arguments);
        args.Add(url);

        var listing = new ProfileListing();
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = WatchListingAsync(listing, progress, running);
        var finished = false;
        try
        {
            var code = await ProcessRunner.RunAsync(tools.GalleryDl, args, listing.Read, running.Token).ConfigureAwait(false);
            finished = code == 0;
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"profile listing stopped with {listing.Files} files");
        }
        finally
        {
            await running.CancelAsync().ConfigureAwait(false);
            await watcher.ConfigureAwait(false);
        }

        var list = Path.Combine(staging.Folder, ListFile);
        var probe = GalleryDlParser.ParseLines(
            File.Exists(list) ? File.ReadLines(list) : [],
            partial: !finished);
        var throughTool = probe.Items.Count(item => item.ToolUrl is not null);
        AppLog.Info($"profile listing: {probe.ImageCount} images, {probe.VideoCount} videos, {throughTool} through the tool, partial {probe.IsPartial}");
        if (probe.Items.Count > 0)
            return probe;

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(ShortError(url, listing.Problems));
    }

    // gallery-dl goes quiet only when something is wrong: a rate-limit wait is
    // shorter than this and it logs a line before it starts.
    private static async Task WatchListingAsync(
        ProfileListing listing,
        IProgress<FetchProgress>? progress,
        CancellationTokenSource running)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(running.Token).ConfigureAwait(false))
            {
                progress?.Report(new FetchProgress { Status = listing.Status(clock.Elapsed) });
                if (!listing.IsStalled(ListingStallLimit))
                    continue;
                AppLog.Warn($"profile listing silent for {ListingStallLimit.TotalMinutes:0} minutes; keeping what was found");
                await running.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The listing ended.
        }
    }

    private const string ListFile = "list.jsonl";

    /// <summary>Only the fields the parser reads: captions and tags on a big profile run to gigabytes.</summary>
    internal static string ListingOption(string folder)
    {
        var writer = new
        {
            name = "metadata",
            @event = "prepare",
            mode = "jsonl",
            filename = ListFile,
            open = "a",
            @private = true,
            include = new[]
            {
                "_url", "url", "display_url", "filename", "extension", "category", "subcategory",
                "type", "title", "description", "content", "thumbnail", "thumbnail_url", "duration"
            }
        };
        using var json = JsonSerializer.SerializeToDocument(writer);
        var fields = new Dictionary<string, JsonElement> { ["base-directory"] = JsonSerializer.SerializeToElement(folder) };
        foreach (var property in json.RootElement.EnumerateObject())
            fields[property.Name] = property.Value.Clone();
        return "postprocessors=" + JsonSerializer.Serialize(new[] { fields });
    }

    private static async Task ShowElapsedAsync(string status, IProgress<FetchProgress>? progress, CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                progress?.Report(new FetchProgress { Status = $"{status} {clock.Elapsed:m\\:ss}" });
        }
        catch (OperationCanceledException)
        {
            // Reading finished.
        }
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
            var posts = ToolPosts(dest, probe, duplicate);
            var grandTotal = jobs.Count + posts.Count;
            AppLog.Info($"download plan: {jobs.Count} direct, {posts.Count} through the tool, as {duplicate}");

            // Photos first: they are the fast part. A failure there must not cost the videos.
            InvalidOperationException? failed = null;
            try
            {
                if (jobs.Count > 0)
                    await DownloadDirectAsync(jobs, progress, cancellationToken, grandTotal).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                failed = ex;
            }

            if (posts.Count > 0)
            {
                await DownloadWithToolAsync(posts, dest, grandTotal, duplicate, false, progress, cancellationToken, offset: jobs.Count, videosOnly: true)
                    .ConfigureAwait(false);
            }

            if (failed is not null)
                throw failed;
            return;
        }

        await DownloadWithToolAsync([url], dest, probe.FileCount, duplicate, wholeProfile, progress, cancellationToken).ConfigureAwait(false);
    }

    // Everything a signed-in account can see on a profile, as files. No "info":
    // that is metadata, not media. The short tabs come first, so a stopped run
    // still has the stories, which disappear within a day.
    internal const string InstagramEverything = "avatar,stories,highlights,posts,reels,tagged";

    internal const string InstagramFileName = "{filename}.{extension}";

    // A post can mix photos and video; the photos already came from the list.
    internal const string VideosOnlyFilter = "extension not in ('jpg', 'jpeg', 'png', 'webp', 'heic', 'gif')";

    internal const int DirectParallel = 4;

    internal static readonly TimeSpan ReadLimit = TimeSpan.FromMinutes(30);

    // A whole profile has no total limit: the Stop button is the user's limit.
    internal static readonly TimeSpan ListingStallLimit = TimeSpan.FromMinutes(20);

    // Fetch lists every file, so Download only needs a way to get each of them.
    internal static bool CanDownloadDirect(MediaProbe probe)
        => probe.Items.Count > 0
           && probe.Items.All(item => MediaRouter.TryParseHttpUrl(item.DownloadUrl, out _) || item.ToolUrl is not null);

    /// <summary>Posts whose media only the tool can fetch, minus those already saved.</summary>
    internal static IReadOnlyList<string> ToolPosts(string dest, MediaProbe probe, DuplicateChoice duplicate)
        => probe.Items
            .Where(item => item.ToolUrl is not null
                           && !(duplicate == DuplicateChoice.Skip
                                && item.SaveName is not null
                                && File.Exists(Path.Combine(dest, item.SaveName))))
            .Select(item => item.ToolUrl!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

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
        CancellationToken cancellationToken,
        int grandTotal = 0)
    {
        using var handler = HttpFetch.CreateHandler(maxConnectionsPerServer: DirectParallel);
        using var http = HttpFetch.CreateClient(handler, Timeout.InfiniteTimeSpan);
        var leftover = jobs.ToList();
        var total = jobs.Count;
        var shownTotal = Math.Max(grandTotal, total);
        var done = 0;
        var bytes = 0L;
        var clock = System.Diagnostics.Stopwatch.StartNew();

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
                            var saved = Interlocked.Add(ref bytes, new FileInfo(job.Path).Length);
                            progress.Report(new FetchProgress
                            {
                                Percent = 100.0 * n / shownTotal,
                                HasPercent = true,
                                Status = BatchStatus(n, shownTotal, saved, clock.Elapsed)
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
        IReadOnlyList<string> urls,
        string dest,
        int fileCount,
        DuplicateChoice duplicate,
        bool wholeProfile,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken,
        int offset = 0,
        bool videosOnly = false)
    {
        var url = urls[0];
        var tools = await ToolBootstrapper.EnsureAsync(new Progress<string>(_ =>
        {
            progress.Report(new FetchProgress { Status = "tools" });
        }), cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(tools.GalleryDl))
            throw new InvalidOperationException(ShortError(url, ""));

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

        // Same names as the list, or repeat runs cannot recognise what is saved.
        if (wholeProfile || videosOnly)
            args.AddRange(["-o", "filename=" + InstagramFileName]);
        if (videosOnly)
            args.AddRange(["--filter", VideosOnlyFilter, "-o", "extractor.instagram.sleep-request=1.0-2.0"]);

        // A profile's videos can be too many links for one command line.
        using var links = new LinkFile(urls);
        if (urls.Count > 1)
            args.AddRange(["--input-file", links.Path]);
        else
            args.Add(url);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var moving = new CancellationTokenSource();
        var mover = MoveWhileRunningAsync(staging, dest, duplicate, moving.Token);

        int code;
        try
        {
            code = await ProcessRunner.RunAsync(tools.GalleryDl, args, line =>
            {
                if (LooksLikeSavedFile(line))
                {
                    done++;
                    // gallery-dl can report a video more than once; never pass the known total.
                    var total = videosOnly ? fileCount : Math.Max(fileCount, offset + done);
                    var shown = Math.Min(offset + done, total);
                    progress.Report(new FetchProgress
                    {
                        Percent = total == 0 ? 0 : 100.0 * shown / total,
                        HasPercent = true,
                        Status = BatchStatus(shown, total, 0, clock.Elapsed)
                    });
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            moving.Cancel();
            await mover.ConfigureAwait(false);
            staging.MoveTo(dest, duplicate); // Keep what finished before Stop.
            throw;
        }

        moving.Cancel();
        await mover.ConfigureAwait(false);
        progress.Report(new FetchProgress { Status = "Saving…" });
        done = Math.Max(done, staging.MoveTo(dest, duplicate));
        EnsureToolSucceeded(code, done, fileCount);
    }

    /// <summary>"12 / 200 · 3.1 MB/s · about 2 min left", as far as it is known yet.</summary>
    internal static string BatchStatus(int done, int total, long bytes, TimeSpan elapsed)
    {
        var parts = new List<string> { $"{done} / {total}" };
        if (bytes > 0 && elapsed >= TimeSpan.FromSeconds(1))
            parts.Add(YtDlpParser.FormatSpeed(bytes / elapsed.TotalSeconds));
        if (done > 0 && done < total && elapsed >= TimeSpan.FromSeconds(3))
        {
            var seconds = elapsed.TotalSeconds / done * (total - done);
            parts.Add(seconds < 60
                ? L.Format("about {0} s left", Math.Max(5, (int)Math.Ceiling(seconds / 5) * 5))
                : L.Format("about {0} min left", (int)Math.Ceiling(seconds / 60)));
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>Hand finished files over as they land, so Stop or a crash loses nothing.</summary>
    private static async Task MoveWhileRunningAsync(ToolStaging staging, string dest, DuplicateChoice duplicate, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    staging.MoveTo(dest, duplicate);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    // The tool still holds that file; the last move reports real problems.
                    AppLog.Info($"Will retry moving files: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The tool finished.
        }
    }

    private sealed class LinkFile : IDisposable
    {
        public string Path { get; }

        public LinkFile(IReadOnlyList<string> urls)
        {
            Path = urls.Count > 1 ? System.IO.Path.GetTempFileName() : "";
            if (urls.Count > 1)
                File.WriteAllLines(Path, urls);
        }

        public void Dispose()
        {
            if (Path.Length > 0)
                HttpFetch.TryDelete(Path);
        }
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
