using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FetchIt.Models;

namespace FetchIt.Services;

/// <summary>
/// kemono / coomer reader. Lists every post of a creator through the public API
/// (50 posts per page, no cap) and saves each post into its own sub folder:
/// &lt;creator&gt;/&lt;post title&gt; [id]/NN_name.ext
/// </summary>
public sealed class KemonoService
{
    internal const int PageSize = 50;
    internal const int Parallel = 6;
    internal const string BlockedMessage = "Kemono's media servers are not responding. Try again later.";

    // Same set the site thumbnailer accepts (gallery-dl util.EXTS_IMAGE).
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "bmp", "webp"
    };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "webm", "mkv", "mov", "m4v", "avi"
    };

    internal sealed record Target(string Root, string Service, string User, string? PostId, string? Query);

    internal sealed record Job(string Url, string? Fallback, string Path);

    /// <summary>https://kemono.cr style root for any kemono/coomer host (img., n1., www. ...), else null.</summary>
    public static string? SiteRoot(string host)
    {
        var parts = host.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
        if (parts.Length < 2)
            return null;
        var name = parts[^2];
        var tld = parts[^1];
        if (name is not ("kemono" or "coomer") || tld is not ("cr" or "su" or "st" or "party"))
            return null;
        return $"https://{name}.{tld}";
    }

    internal static bool TryParse(string url, out Target target)
    {
        target = null!;
        if (!MediaRouter.TryParseHttpUrl(url, out var uri) || SiteRoot(uri.Host) is not { } root)
            return false;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !parts[1].Equals("user", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("discord", StringComparison.OrdinalIgnoreCase))
            return false;

        string? postId = null;
        if (parts.Length >= 5 && parts[3].Equals("post", StringComparison.OrdinalIgnoreCase))
            postId = parts[4];
        else if (parts.Length > 3)
            return false;

        string? query = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "q" && kv[1].Length > 0)
                query = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
        }

        target = new Target(root, parts[0], parts[2], postId, query);
        return true;
    }

    public async Task<MediaProbe> ProbeAsync(
        string url,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryParse(url, out var target))
            throw new InvalidOperationException("Paste a kemono creator or post link.");

        progress?.Report(new FetchProgress { Status = "Reading posts…" });
        using var http = CreateApiClient();
        var api = $"{target.Root}/api/v1/{Uri.EscapeDataString(target.Service)}/user/{Uri.EscapeDataString(target.User)}";
        var posts = new List<JsonElement>();
        string creator;
        try
        {
            creator = await ReadCreatorAsync(http, api, cancellationToken).ConfigureAwait(false) ?? target.User;
            if (target.PostId is not null)
            {
                var root = await GetJsonAsync(http, $"{api}/post/{Uri.EscapeDataString(target.PostId)}", cancellationToken)
                    .ConfigureAwait(false);
                posts.Add(root.ValueKind == JsonValueKind.Object
                          && root.TryGetProperty("post", out var post)
                          && post.ValueKind == JsonValueKind.Object
                    ? post
                    : root);
            }
            else
            {
                await ReadAllPostsAsync(http, api, target.Query, posts, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(ex.StatusCode == HttpStatusCode.NotFound
                ? "That creator was not found."
                : "Could not reach kemono. Try again.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Could not read that link.");
        }

        var probe = BuildProbe(target, creator, posts);
        if (probe.Items.Count == 0)
            throw new InvalidOperationException("No photos or videos in that link.");
        return probe;
    }

    private static async Task ReadAllPostsAsync(
        HttpClient http,
        string api,
        string? query,
        List<JsonElement> posts,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var q = query is null ? "" : "&q=" + Uri.EscapeDataString(query);
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await GetJsonAsync(http, $"{api}/posts?o={offset}{q}", cancellationToken).ConfigureAwait(false);
            if (page.ValueKind == JsonValueKind.Object && page.TryGetProperty("posts", out var wrapped))
                page = wrapped;
            if (page.ValueKind != JsonValueKind.Array)
                break;

            var count = 0;
            var fresh = 0;
            foreach (var post in page.EnumerateArray())
            {
                count++;
                var id = ReadString(post, "id");
                if (id is null || seen.Add(id))
                {
                    posts.Add(post);
                    fresh++;
                }
            }

            progress?.Report(new FetchProgress { Status = $"Reading posts… {posts.Count}" });
            // A short page is the last one; a page with nothing new means the API ignored the offset.
            if (count < PageSize || fresh == 0)
                break;
        }
    }

    internal static MediaProbe BuildProbe(Target target, string creator, IReadOnlyList<JsonElement> posts)
    {
        var items = new List<MediaItem>();
        var creatorFolder = MediaRouter.SanitizeFolderName(creator);
        var thumbRoot = target.Root.Replace("://", "://img.") + "/thumbnail/data";
        string? firstTitle = null;

        foreach (var post in posts)
        {
            var id = ReadString(post, "id") ?? "post";
            var title = ReadString(post, "title");
            if (string.IsNullOrWhiteSpace(title))
                title = $"post {id}";
            firstTitle ??= title;
            var postFolder = $"{MediaRouter.SanitizeFolderName(title)} [{MediaRouter.SanitizeFolderName(id)}]";
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var n = 0;

            foreach (var file in Files(post))
            {
                var path = ReadString(file, "path")?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(path)
                    || !path.StartsWith('/')
                    || path.Contains("..", StringComparison.Ordinal)
                    || !seenPaths.Add(path))
                    continue;

                var name = ReadString(file, "name");
                var pathExt = Ext(path);
                var ext = Ext(name) is { Length: > 0 } nameExt ? nameExt : pathExt;
                var isVideo = VideoExt.Contains(ext);
                if (!isVideo && !ImageExt.Contains(ext))
                    continue;

                n++;
                var stem = MediaRouter.SanitizeFolderName(
                    Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(name) ? path : name));
                var thumb = !isVideo && ImageExt.Contains(pathExt) ? thumbRoot + path : null;
                items.Add(new MediaItem
                {
                    Kind = isVideo ? MediaKind.Video : MediaKind.Image,
                    Title = title,
                    ThumbnailUrl = thumb,
                    DownloadUrl = $"{target.Root}/data{path}",
                    FallbackUrl = thumb,
                    SaveName = Path.Combine(creatorFolder, postFolder, $"{n:D2}_{stem}.{ext.ToLowerInvariant()}")
                });
            }
        }

        return new MediaProbe
        {
            Title = target.PostId is not null && firstTitle is not null ? firstTitle : creator,
            Site = target.Root.Contains("coomer", StringComparison.Ordinal) ? "Coomer" : "Kemono",
            VideoCount = items.Count(i => i.Kind == MediaKind.Video),
            ImageCount = items.Count(i => i.Kind == MediaKind.Image),
            Engine = EngineKind.GalleryDl,
            Items = items
        };
    }

    /// <summary>
    /// Saves every item. Originals come from the n*.kemono servers; when those are unreachable
    /// (blocked by some ISPs) images fall back to the site preview. Returns a note when previews were used.
    /// </summary>
    public async Task<string?> DownloadAsync(
        string folder,
        MediaProbe probe,
        DuplicateChoice duplicate,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var dest = Path.GetFullPath(folder);
        if (!FolderStore.CanWrite(dest))
            throw new InvalidOperationException("Windows blocked that folder. Pick another save folder.");

        var jobs = PlanJobs(dest, probe, duplicate);
        if (jobs.Count == 0)
            throw new InvalidOperationException(duplicate == DuplicateChoice.Skip
                ? "Those files are already saved."
                : "Could not save those files.");

        using var handler = HttpFetch.CreateHandler(maxConnectionsPerServer: Parallel);
        handler.ConnectTimeout = TimeSpan.FromSeconds(3);
        handler.AllowAutoRedirect = true;
        using var http = HttpFetch.CreateClient(handler, Timeout.InfiniteTimeSpan);

        var total = jobs.Count;
        var done = 0;
        var previews = 0;
        var originalsDown = 0;
        var failed = new ConcurrentBag<Job>();
        progress.Report(new FetchProgress { Status = $"0 / {total}" });

        await System.Threading.Tasks.Parallel.ForEachAsync(
            jobs,
            new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = cancellationToken },
            async (job, token) =>
            {
                var saved = false;
                if (Volatile.Read(ref originalsDown) == 0)
                {
                    try
                    {
                        await HttpFetch.SaveStreamAsync(http, job.Url, job.Path, token, retries: 1).ConfigureAwait(false);
                        saved = true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (IsUnreachable(ex))
                            Interlocked.Exchange(ref originalsDown, 1);
                    }
                }

                if (!saved && job.Fallback is not null)
                {
                    try
                    {
                        await HttpFetch.SaveStreamAsync(http, job.Fallback, job.Path, token).ConfigureAwait(false);
                        saved = true;
                        Interlocked.Increment(ref previews);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                    }
                }

                if (!saved)
                {
                    failed.Add(job);
                    return;
                }

                var n = Interlocked.Increment(ref done);
                progress.Report(new FetchProgress
                {
                    Percent = 100.0 * n / total,
                    HasPercent = true,
                    Status = $"{n} / {total}"
                });
            }).ConfigureAwait(false);

        if (done == 0)
            throw new InvalidOperationException(originalsDown == 1 ? BlockedMessage : "Could not save those files.");
        if (!failed.IsEmpty)
            throw new InvalidOperationException($"Saved {done} of {total} files.");
        return null;
    }

    internal static string PreviewNote(int done, int previews)
        => previews >= done
            ? $"Saved {done} files as previews. kemono's full-size server is blocked on this network; use a VPN for originals."
            : $"Saved {done} files ({previews} as previews). kemono's full-size server is blocked on this network; use a VPN for originals.";

    internal static List<Job> PlanJobs(string dest, MediaProbe probe, DuplicateChoice duplicate)
    {
        var root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var names = SaveClash.PlannedNames(probe);
        var jobs = new List<Job>(probe.Items.Count);
        for (var i = 0; i < probe.Items.Count && i < names.Count; i++)
        {
            var item = probe.Items[i];
            if (item.DownloadUrl is null || !MediaRouter.TryParseHttpUrl(item.DownloadUrl, out _))
                continue;

            var path = Path.GetFullPath(Path.Combine(dest, names[i]));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (duplicate == DuplicateChoice.Skip && File.Exists(path))
                continue;
            if (duplicate == DuplicateChoice.KeepBoth)
                path = SaveClash.UniquePath(dest, names[i]);

            var fallback = item.FallbackUrl is not null && MediaRouter.TryParseHttpUrl(item.FallbackUrl, out _)
                ? item.FallbackUrl
                : null;
            jobs.Add(new Job(item.DownloadUrl, fallback, path));
        }

        return jobs;
    }

    internal static bool IsUnreachable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException or TimeoutException or TaskCanceledException)
                return true;
            if (e is HttpRequestException
                {
                    HttpRequestError: HttpRequestError.ConnectionError
                    or HttpRequestError.NameResolutionError
                    or HttpRequestError.SecureConnectionError
                })
                return true;
        }

        return false;
    }

    private static IEnumerable<JsonElement> Files(JsonElement post)
    {
        if (post.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
            yield return file;
        if (post.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                if (attachment.ValueKind == JsonValueKind.Object)
                    yield return attachment;
            }
        }
    }

    private static HttpClient CreateApiClient()
    {
        var handler = HttpFetch.CreateHandler(decompression: DecompressionMethods.All);
        var http = HttpFetch.CreateClient(handler, TimeSpan.FromSeconds(30));
        // The API answers JSON only with this header (same trick gallery-dl uses).
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/css");
        return http;
    }

    private static async Task<string?> ReadCreatorAsync(HttpClient http, string api, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await GetJsonAsync(http, $"{api}/profile", cancellationToken).ConfigureAwait(false);
            return ReadString(profile, "name");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        var text = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string Ext(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var ext = Path.GetExtension(name.Split('?')[0]).Trim('.');
        return ext.Length is > 0 and < 8 ? ext.ToLowerInvariant() : "";
    }

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
