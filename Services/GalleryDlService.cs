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
        HttpClient? http)
    {
        var owns = http is null;
        SocketsHttpHandler? handler = null;
        if (owns)
        {
            handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = DirectParallel,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
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
                        await SaveOneAsync(client, job.Url, job.Path, token).ConfigureAwait(false);
                        var n = Interlocked.Increment(ref done);
                        progress.Report(new FetchProgress
                        {
                            Percent = 100.0 * n / total,
                            HasPercent = true,
                            Status = $"{n} / {total}"
                        });
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
            var firstParallel = total <= 1 ? 1 : Math.Min(DirectParallel, total);
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

    private static async Task SaveOneAsync(
        HttpClient http,
        string mediaUrl,
        string name,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(400 * attempt, cancellationToken).ConfigureAwait(false);
            try
            {
                await SaveOneOnceAsync(http, mediaUrl, name, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("Could not save that file.");
    }

    private static async Task SaveOneOnceAsync(
        HttpClient http,
        string mediaUrl,
        string name,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
        var referer = ThumbnailUrl.RefererFor(mediaUrl);
        if (referer is not null)
            request.Headers.Referrer = referer;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var tmp = name + ".part";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(tmp))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            File.Move(tmp, name, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
            }

            throw;
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
