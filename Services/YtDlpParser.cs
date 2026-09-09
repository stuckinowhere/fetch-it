using System.Text.Json;
using FetchIt.Models;

namespace FetchIt.Services;

public static class YtDlpParser
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "webp", "gif", "bmp", "avif", "heic"
    };

    public static MediaProbe Parse(string json, bool needsLogin = false)
    {
        using var doc = JsonDocument.Parse(FindFirstObject(json));
        var root = doc.RootElement;
        var type = root.TryGetProperty("_type", out var typeEl) ? typeEl.GetString() : "video";

        if (string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase))
            return ParsePlaylist(root, needsLogin);

        return ParseEntry(root, needsLogin);
    }

    public static double? TryParsePercent(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+(?:\.\d+)?)%");
        if (!match.Success)
            return null;
        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return null;
        return Math.Clamp(value, 0, 100);
    }

    public static string? TryParseSizeStatus(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line,
            @"(\d+(?:\.\d+)?\s*[KMG]i?B)\s*/\s*(\d+(?:\.\d+)?\s*[KMG]i?B)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? $"{match.Groups[1].Value.Trim()} / {match.Groups[2].Value.Trim()}" : null;
    }

    private static MediaProbe ParsePlaylist(JsonElement root, bool needsLogin)
    {
        var title = ReadString(root, "title") ?? "playlist";
        var site = ExtractorName(root);
        var videos = 0;
        var images = 0;
        TimeSpan? duration = null;

        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                if (IsImage(entry))
                    images++;
                else
                    videos++;
                duration = AddDuration(duration, ReadDuration(entry));
            }
        }

        if (videos == 0 && images == 0)
            videos = 1;

        return new MediaProbe
        {
            Title = title,
            Site = site,
            Duration = duration,
            VideoCount = videos,
            ImageCount = images,
            NeedsLogin = needsLogin,
            Engine = EngineKind.YtDlp
        };
    }

    private static MediaProbe ParseEntry(JsonElement root, bool needsLogin)
    {
        var image = IsImage(root);
        return new MediaProbe
        {
            Title = ReadString(root, "title") ?? ReadString(root, "id") ?? "video",
            Site = ExtractorName(root),
            Duration = ReadDuration(root),
            VideoCount = image ? 0 : 1,
            ImageCount = image ? 1 : 0,
            NeedsLogin = needsLogin,
            Engine = EngineKind.YtDlp
        };
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
            "Youtube" => "YouTube",
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
