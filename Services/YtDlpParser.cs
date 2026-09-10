using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FetchIt.Models;

namespace FetchIt.Services;

public static class YtDlpParser
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "webp", "gif", "bmp", "avif", "heic"
    };

    public static MediaProbe Parse(string json)
    {
        using var doc = JsonDocument.Parse(FindFirstObject(json));
        var root = doc.RootElement;
        var type = root.TryGetProperty("_type", out var typeEl) ? typeEl.GetString() : "video";

        if (string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase))
            return ParsePlaylist(root);

        return ParseEntry(root);
    }

    private static readonly Regex PercentRegex = new(@"(\d+(?:\.\d+)?)%", RegexOptions.CultureInvariant);
    private static readonly Regex SpeedRegex = new(
        @"at\s+(\d+(?:\.\d+)?)\s*([KMGT]i?B)/s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OfSizeRegex = new(
        @"of\s+~?\s*(\d+(?:\.\d+)?)\s*([KMGT]i?B)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RatioRegex = new(
        @"(\d+(?:\.\d+)?)\s*([KMGT]i?B)\s*/\s*(\d+(?:\.\d+)?)\s*([KMGT]i?B)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public readonly record struct DownloadProgressLine(double? Percent, string Status);

    public static double? TryParsePercent(string line)
    {
        var match = PercentRegex.Match(line);
        if (!match.Success)
            return null;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return Math.Clamp(value, 0, 100);
    }

    public static string? TryParseSizeStatus(string line)
    {
        var match = RatioRegex.Match(line);
        return match.Success ? $"{match.Groups[1].Value.Trim()} {match.Groups[2].Value.Trim()} / {match.Groups[3].Value.Trim()} {match.Groups[4].Value.Trim()}" : null;
    }

    public static DownloadProgressLine? TryParseDownloadProgress(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Merging formats", StringComparison.OrdinalIgnoreCase))
            return new DownloadProgressLine(100, "Merging…");

        var percent = TryParsePercent(line);
        var speed = TryParseSpeedBytes(line);
        var remaining = TryParseRemainingBytes(line, percent);
        if (percent is null && speed is null && remaining is null)
            return null;

        var parts = new List<string>();
        if (speed is > 0)
            parts.Add(FormatSpeed(speed.Value));
        if (remaining is > 0 && percent is not 100)
            parts.Add(FormatLeft(remaining.Value));

        var status = parts.Count > 0
            ? string.Join("  ·  ", parts)
            : percent is >= 100 ? "Finishing…" : percent is double p ? $"{p.ToString("0", CultureInfo.InvariantCulture)}%" : "";
        return new DownloadProgressLine(percent, status);
    }

    private static double? TryParseSpeedBytes(string line)
    {
        var match = SpeedRegex.Match(line);
        if (!match.Success)
            return null;
        return ToBytes(match.Groups[1].Value, match.Groups[2].Value);
    }

    private static double? TryParseRemainingBytes(string line, double? percent)
    {
        var of = OfSizeRegex.Match(line);
        if (of.Success && percent is double p)
            return Math.Max(0, ToBytes(of.Groups[1].Value, of.Groups[2].Value) * (100 - p) / 100.0);

        var ratio = RatioRegex.Match(line);
        if (!ratio.Success)
            return null;
        var downloaded = ToBytes(ratio.Groups[1].Value, ratio.Groups[2].Value);
        var total = ToBytes(ratio.Groups[3].Value, ratio.Groups[4].Value);
        return Math.Max(0, total - downloaded);
    }

    private static double ToBytes(string number, string unit)
    {
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;
        var factor = unit.Trim().ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" or "KIB" => 1024d,
            "MB" or "MIB" => 1024d * 1024d,
            "GB" or "GIB" => 1024d * 1024d * 1024d,
            "TB" or "TIB" => 1024d * 1024d * 1024d * 1024d,
            _ => 1d
        };
        return value * factor;
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        var mb = bytesPerSecond / (1024d * 1024d);
        if (mb >= 0.1)
            return $"{mb.ToString("0.0", CultureInfo.InvariantCulture)} MB/s";
        var kb = bytesPerSecond / 1024d;
        return $"{kb.ToString("0", CultureInfo.InvariantCulture)} KB/s";
    }

    private static string FormatLeft(double bytes)
    {
        var mb = bytes / (1024d * 1024d);
        if (mb >= 10)
            return $"{mb.ToString("0", CultureInfo.InvariantCulture)} MB left";
        if (mb >= 0.1)
            return $"{mb.ToString("0.0", CultureInfo.InvariantCulture)} MB left";
        return "less than 0.1 MB left";
    }

    private static MediaProbe ParsePlaylist(JsonElement root)
    {
        var title = ReadString(root, "title") ?? "playlist";
        var site = ExtractorName(root);
        var items = new List<MediaItem>();
        TimeSpan? duration = null;

        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                var item = ToItem(entry);
                items.Add(item);
                duration = AddDuration(duration, item.Duration);
            }
        }

        if (items.Count == 0)
            items.Add(ToItem(root, titleFallback: title));

        return new MediaProbe
        {
            Title = title,
            Site = site,
            Duration = duration,
            VideoCount = items.Count(item => item.Kind == MediaKind.Video),
            ImageCount = items.Count(item => item.Kind == MediaKind.Image),
            Engine = EngineKind.YtDlp,
            Items = items
        };
    }

    private static MediaProbe ParseEntry(JsonElement root)
    {
        var item = ToItem(root);
        return new MediaProbe
        {
            Title = item.Title,
            Site = ExtractorName(root),
            Duration = item.Duration,
            VideoCount = item.Kind == MediaKind.Video ? 1 : 0,
            ImageCount = item.Kind == MediaKind.Image ? 1 : 0,
            Engine = EngineKind.YtDlp,
            Items = [item]
        };
    }

    private static MediaItem ToItem(JsonElement root, string? titleFallback = null)
    {
        var image = IsImage(root);
        return new MediaItem
        {
            Kind = image ? MediaKind.Image : MediaKind.Video,
            Title = ReadString(root, "title") ?? ReadString(root, "id") ?? titleFallback ?? "video",
            Duration = ReadDuration(root),
            ThumbnailUrl = ReadThumbnail(root)
        };
    }

    private static string? ReadThumbnail(JsonElement root)
    {
        var direct = ReadString(root, "thumbnail");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (!root.TryGetProperty("thumbnails", out var thumbs) || thumbs.ValueKind != JsonValueKind.Array)
            return null;

        string? best = null;
        var bestSize = -1;
        foreach (var thumb in thumbs.EnumerateArray())
        {
            if (thumb.ValueKind != JsonValueKind.Object)
                continue;
            var url = ReadString(thumb, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var size = 0;
            if (thumb.TryGetProperty("width", out var width) && width.TryGetInt32(out var w))
                size = w;
            else if (thumb.TryGetProperty("height", out var height) && height.TryGetInt32(out var h))
                size = h;
            if (size >= bestSize)
            {
                bestSize = size;
                best = url;
            }
        }

        return best;
    }

    private static bool IsImage(JsonElement root)
    {
        var ext = ReadString(root, "ext");
        if (ext is not null && ImageExt.Contains(ext.Trim('.')))
            return true;
        var vcodec = ReadString(root, "vcodec");
        var acodec = ReadString(root, "acodec");
        return string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase)
               && string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase)
               && ext is not null && ImageExt.Contains(ext.Trim('.'));
    }

    private static string ExtractorName(JsonElement root)
    {
        var key = ReadString(root, "extractor_key") ?? ReadString(root, "extractor") ?? "";
        return key switch
        {
            "Youtube" or "YoutubeTab" or "YoutubeYtBe" => "YouTube",
            "Odnoklassniki" => "OK.ru",
            "TikTok" or "TikTokLive" => "TikTok",
            "Dailymotion" => "Dailymotion",
            "Reddit" => "Reddit",
            "Vimeo" => "Vimeo",
            "Facebook" => "Facebook",
            "Twitch" or "TwitchClips" or "TwitchVod" => "Twitch",
            "BiliBili" or "Bilibili" => "Bilibili",
            "Twitter" => "X",
            "Soundcloud" => "SoundCloud",
            "vk" or "VK" => "VK",
            "Generic" => "Video",
            _ => string.IsNullOrWhiteSpace(key) ? "Video" : key
        };
    }

    private static TimeSpan? ReadDuration(JsonElement root)
    {
        if (!root.TryGetProperty("duration", out var duration) || duration.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    private static TimeSpan? AddDuration(TimeSpan? current, TimeSpan? extra)
    {
        if (extra is null)
            return current;
        return (current ?? TimeSpan.Zero) + extra.Value;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FindFirstObject(string json)
    {
        var trimmed = json.Trim();
        if (trimmed.StartsWith('{'))
            return trimmed;

        using var reader = new StringReader(trimmed);
        while (reader.ReadLine() is { } line)
        {
            var row = line.Trim();
            if (row.StartsWith('{'))
                return row;
        }

        return trimmed;
    }
}
