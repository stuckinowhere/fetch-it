using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FetchIt.Models;

namespace FetchIt.Services;

/// <summary>
/// ok.ru / Odnoklassniki reader. yt-dlp 2026.08.19 crashes when flashvars.metadata
/// is already a JSON object (TypeError in _parse_json). Parse the page ourselves.
/// </summary>
public sealed class OkRuService
{
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly string[] QualityOrder =
        ["full", "ultra", "quad", "fullhd", "hd", "sd", "low", "lowest", "mobile"];

    public async Task<MediaProbe> ProbeAsync(
        string url,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!MediaRouter.TryParseHttpUrl(url, out var uri) || !MediaRouter.IsOkRu(uri))
            throw new InvalidOperationException("Not an OK.ru link.");

        var pageUrl = MediaRouter.CanonicalPublicUrl(url);
        progress?.Report(new FetchProgress { Status = "Reading link…" });

        using var http = CreatePageClient();
        using var response = await http.GetAsync(pageUrl, cancellationToken).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException("Could not read that link.");

        if (html.Contains("Access to this video is restricted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That site wants a signed-in session. Close Chrome, then paste the link again.");

        if (!TryParsePlayer(html, out var player))
            throw new InvalidOperationException("Could not read that link.");

        if (player.TryGetProperty("isExternalPlayer", out var external)
            && external.ValueKind is JsonValueKind.True
            && player.TryGetProperty("url", out var embed)
            && embed.ValueKind == JsonValueKind.String
            && MediaRouter.TryParseHttpUrl(embed.GetString(), out _))
        {
            return await new YtDlpService()
                .ProbeAsync(embed.GetString()!, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!player.TryGetProperty("flashvars", out var flashvars)
            || flashvars.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Could not read that link.");

        if (!TryReadMetadata(flashvars, out var metadata))
            throw new InvalidOperationException("Could not read that link.");

        if (metadata.TryGetProperty("paymentInfo", out var pay)
            && pay.ValueKind is JsonValueKind.Object or JsonValueKind.True)
            throw new InvalidOperationException("That video is paid.");

        var qualities = ListQualities(metadata);
        if (qualities.Count == 0)
            throw new InvalidOperationException("Could not read that link.");

        var best = qualities[0];
        var title = ReadTitle(metadata) ?? "OK.ru video";
        var duration = ReadDuration(metadata);
        var thumb = ReadPoster(metadata);

        return new MediaProbe
        {
            Title = title,
            Site = "OK.ru",
            Duration = duration,
            VideoCount = 1,
            ImageCount = 0,
            Engine = EngineKind.GalleryDl,
            Qualities = qualities,
            Items =
            [
                new MediaItem
                {
                    Kind = MediaKind.Video,
                    Title = title,
                    Duration = duration,
                    ThumbnailUrl = thumb,
                    DownloadUrl = best.Url
                }
            ]
        };
    }

    public async Task DownloadAsync(
        string folder,
        MediaProbe probe,
        DuplicateChoice duplicate,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var dest = Path.GetFullPath(folder);
        if (!FolderStore.CanWrite(dest))
            throw new InvalidOperationException("Windows blocked that folder. Pick another save folder.");

        var jobs = GalleryDlService.PlanDirectFiles(dest, probe, duplicate);
        if (jobs.Count == 0)
            throw new InvalidOperationException("Could not save those files.");

        var (url, path) = jobs[0];
        await SaveVideoAsync(url, path, progress, cancellationToken).ConfigureAwait(false);
    }

    public static MediaProbe WithSelectedQuality(MediaProbe probe, string url)
    {
        if (probe.Items.Count == 0)
            return probe;

        var first = probe.Items[0];
        var items = new List<MediaItem>(probe.Items.Count)
        {
            new()
            {
                Kind = first.Kind,
                Title = first.Title,
                Duration = first.Duration,
                ThumbnailUrl = first.ThumbnailUrl,
                DownloadUrl = url
            }
        };
        items.AddRange(probe.Items.Skip(1));
        return new MediaProbe
        {
            Title = probe.Title,
            Site = probe.Site,
            Duration = probe.Duration,
            VideoCount = probe.VideoCount,
            ImageCount = probe.ImageCount,
            Engine = probe.Engine,
            Qualities = probe.Qualities,
            Items = items
        };
    }

    internal static async Task SaveVideoAsync(
        string url,
        string dest,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
        var part = dest + ".part";

        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        using var http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(800 * attempt, cancellationToken).ConfigureAwait(false);

            try
            {
                var have = File.Exists(part) ? new FileInfo(part).Length : 0L;
                using var request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                var referer = ThumbnailUrl.RefererFor(url);
                if (referer is not null)
                    request.Headers.Referrer = referer;
                if (have > 0)
                    request.Headers.Range = new RangeHeaderValue(have, null);

                using var response = await http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    TryDelete(part);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"HTTP {(int)response.StatusCode}",
                        null,
                        response.StatusCode);

                var append = response.StatusCode == HttpStatusCode.PartialContent && have > 0;
                if (!append)
                    have = 0;

                var total = response.Content.Headers.ContentLength is { } length
                    ? (append ? have + length : length)
                    : (long?)null;

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                                 .ConfigureAwait(false))
                await using (var output = append
                                 ? new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None)
                                 : File.Create(part))
                {
                    var meter = new DownloadMeter();
                    meter.Reset(have);
                    var buffer = new byte[256 * 1024];
                    long written = have;
                    int read;
                    var lastReport = 0L;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        written += read;
                        if (written - lastReport < 256 * 1024)
                            continue;
                        lastReport = written;
                        progress.Report(meter.Snapshot(written, total));
                    }
                }

                var finalSize = new FileInfo(part).Length;
                if (finalSize == 0)
                {
                    TryDelete(part);
                    throw new InvalidOperationException("Could not save those files.");
                }

                if (total is { } expected && finalSize < expected)
                    continue;

                File.Move(part, dest, overwrite: true);
                progress.Report(new FetchProgress
                {
                    Percent = 100,
                    HasPercent = true,
                    Status = "Saved"
                });
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

        throw last ?? new InvalidOperationException("Could not save those files.");
    }

    internal static void ReportBytes(IProgress<FetchProgress> progress, long written, long? total)
    {
        var meter = new DownloadMeter();
        meter.Reset(0);
        progress.Report(meter.Snapshot(written, total));
    }

    internal static string FormatBytes(long bytes) => DownloadMeter.FormatBytes(bytes);

    internal static bool TryParsePlayer(string html, out JsonElement player)
    {
        player = default;
        if (!TryExtractPlayerJson(html, out var json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            player = doc.RootElement.Clone();
            return player.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryExtractPlayerJson(string html, out string json)
    {
        json = "";
        const string marker = "data-options=";
        var start = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        var i = start + marker.Length;
        while (i < html.Length && char.IsWhiteSpace(html[i]))
            i++;
        if (i >= html.Length)
            return false;

        var quote = html[i];
        if (quote is not ('"' or '\''))
            return false;

        i++;
        var end = html.IndexOf(quote, i);
        if (end < 0)
            return false;

        json = WebUtility.HtmlDecode(html[i..end]).Trim();
        return json.Length > 1 && json[0] == '{';
    }

    internal static bool TryReadMetadata(JsonElement flashvars, out JsonElement metadata)
    {
        metadata = default;
        if (!flashvars.TryGetProperty("metadata", out var raw))
            return false;

        if (raw.ValueKind == JsonValueKind.Object)
        {
            metadata = raw.Clone();
            return true;
        }

        if (raw.ValueKind == JsonValueKind.String)
        {
            var text = raw.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return false;
            try
            {
                using var doc = JsonDocument.Parse(text);
                metadata = doc.RootElement.Clone();
                return metadata.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    internal static IReadOnlyList<MediaQuality> ListQualities(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("videos", out var videos)
            || videos.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<(MediaQuality Quality, int Score)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in videos.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl)
                || urlEl.ValueKind != JsonValueKind.String)
                continue;
            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url) || !MediaRouter.TryParseHttpUrl(url, out _))
                continue;
            if (!seen.Add(url))
                continue;

            var name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? ""
                : "";
            list.Add((new MediaQuality { Label = FormatQualityLabel(name), Url = url }, QualityScore(name)));
        }

        return list
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Quality.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Quality)
            .ToList();
    }

    internal static string? PickBestVideoUrl(JsonElement metadata)
        => ListQualities(metadata).FirstOrDefault()?.Url;

    internal static string FormatQualityLabel(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        return key switch
        {
            "full" or "fullhd" or "full_hd" => "Full HD",
            "ultra" or "quad" => "4K",
            "hd" => "HD",
            "sd" => "SD",
            "low" => "Low",
            "lowest" => "Lowest",
            "mobile" => "Mobile",
            "" => "Video",
            _ => char.ToUpperInvariant(name.Trim()[0]) + name.Trim()[1..].ToLowerInvariant()
        };
    }

    internal static int QualityScore(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        for (var i = 0; i < QualityOrder.Length; i++)
        {
            if (key.Contains(QualityOrder[i], StringComparison.Ordinal))
                return QualityOrder.Length - i;
        }

        return 0;
    }

    private static string? ReadTitle(JsonElement metadata)
    {
        if (metadata.TryGetProperty("movie", out var movie)
            && movie.ValueKind == JsonValueKind.Object
            && movie.TryGetProperty("title", out var title)
            && title.ValueKind == JsonValueKind.String)
            return title.GetString();
        return null;
    }

    private static string? ReadPoster(JsonElement metadata)
    {
        if (metadata.TryGetProperty("movie", out var movie)
            && movie.ValueKind == JsonValueKind.Object
            && movie.TryGetProperty("poster", out var poster)
            && poster.ValueKind == JsonValueKind.String)
        {
            var url = poster.GetString();
            return MediaRouter.TryParseHttpUrl(url, out _) ? url : null;
        }

        return null;
    }

    private static TimeSpan? ReadDuration(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("movie", out var movie)
            || movie.ValueKind != JsonValueKind.Object
            || !movie.TryGetProperty("duration", out var duration))
            return null;

        if (duration.ValueKind == JsonValueKind.Number
            && duration.TryGetDouble(out var seconds))
            return TimeSpan.FromSeconds(seconds);

        if (duration.ValueKind == JsonValueKind.String
            && double.TryParse(duration.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out seconds))
            return TimeSpan.FromSeconds(seconds);

        return null;
    }

    private static HttpClient CreatePageClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return http;
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
