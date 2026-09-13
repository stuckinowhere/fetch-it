using System.Text.Json;
using FetchIt.Models;

namespace FetchIt.Services;

public static class GalleryDlParser
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "webp", "gif", "bmp", "avif", "heic"
    };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "webm", "mkv", "mov", "m4v", "avi", "m3u8"
    };

    public static MediaProbe Parse(string json)
    {
        var parsed = ParseItems(json);
        var title = parsed.Select(item => item.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "post";
        var site = parsed.Select(item => item.Site).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "Gallery";
        var items = parsed.Select(item => item.Media).ToList();
        var videos = items.Count(item => item.Kind == MediaKind.Video);
        var images = items.Count(item => item.Kind == MediaKind.Image);

        return new MediaProbe
        {
            Title = title,
            Site = site,
            VideoCount = videos,
            ImageCount = images,
            Engine = EngineKind.GalleryDl,
            Items = items
        };
    }

    internal static List<(MediaItem Media, string Title, string Site)> ParseItems(string json)
    {
        var found = new List<(MediaItem, string, string)>();
        var trimmed = json.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return found;

        if (trimmed.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(trimmed);
            AddArray(doc.RootElement, found);
            return found;
        }

        foreach (var line in trimmed.Split('\n'))
        {
            var row = line.Trim();
            if (!row.StartsWith('{') && !row.StartsWith('['))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(row);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    AddArray(doc.RootElement, found);
                else
                    TryAdd(doc.RootElement, found);
            }
            catch (JsonException)
            {
            }
        }

        return found;
    }

    private static void AddArray(JsonElement array, List<(MediaItem, string, string)> found)
    {
        if (array.GetArrayLength() == 0)
            return;

        if (array[0].ValueKind == JsonValueKind.Number && array[0].TryGetInt32(out var code))
        {
            if (code == 3)
                TryAddDumpFile(array, found);
            return;
        }

        if (array.GetArrayLength() == 2
            && array[0].ValueKind == JsonValueKind.String
            && array[1].ValueKind == JsonValueKind.Object)
        {
            TryAdd(array[1], found, array[0].GetString());
            return;
        }

        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Array)
                AddArray(el, found);
            else
                TryAdd(el, found);
        }
    }

    private static void TryAddDumpFile(JsonElement array, List<(MediaItem, string, string)> found)
    {
        if (array.GetArrayLength() >= 3
            && array[1].ValueKind == JsonValueKind.String
            && array[2].ValueKind == JsonValueKind.Object)
        {
            TryAdd(array[2], found, array[1].GetString());
            return;
        }

        if (array.GetArrayLength() == 2 && array[1].ValueKind == JsonValueKind.Object)
            TryAdd(array[1], found);
    }

    private static void TryAdd(JsonElement el, List<(MediaItem, string, string)> found, string? urlHint = null)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return;

        var url = urlHint
                  ?? ReadString(el, "url")
                  ?? ReadString(el, "link")
                  ?? ReadString(el, "directLink")
                  ?? ReadString(el, "display_url");
        var ext = ReadString(el, "extension") ?? ReadString(el, "ext") ?? ExtFromUrl(url);
        var isVideo = IsVideo(ReadString(el, "type"), ext);
        var title = ReadString(el, "title")
                    ?? ReadString(el, "content")
                    ?? ReadString(el, "description")
                    ?? ReadString(el, "filename")
                    ?? "post";
        var site = PrettySite(ReadString(el, "category") ?? ReadString(el, "subcategory"));
        var item = new MediaItem
        {
            Kind = isVideo ? MediaKind.Video : MediaKind.Image,
            Title = title,
            Duration = ReadDuration(el),
            ThumbnailUrl = ThumbnailFor(el, url, isVideo),
            DownloadUrl = HttpsOrNull(url)
        };
        found.Add((item, title, site));
    }

    private static string? ThumbnailFor(JsonElement payload, string? mediaUrl, bool isVideo)
    {
        var picked = ThumbnailUrl.Pick(
            ReadString(payload, "thumbnail"),
            ReadString(payload, "thumbnail_url"),
            mediaUrl,
            ReadString(payload, "display_url"));
        if (picked is not null)
            return picked;
        return isVideo ? null : ThumbnailUrl.Pick(mediaUrl);
    }

    private static TimeSpan? ReadDuration(JsonElement root)
    {
        if (!root.TryGetProperty("duration", out var duration) || duration.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    private static string ExtFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        try
        {
            return Path.GetExtension(new Uri(url).AbsolutePath).Trim('.');
        }
        catch
        {
            return "";
        }
    }

    private static bool IsVideo(string? type, string ext)
    {
        if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "animated_gif", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(type, "photo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
            return false;

        ext = ext.Trim().Trim('.');
        if (VideoExt.Contains(ext))
            return true;
        return !ImageExt.Contains(ext) && VideoExt.Contains(ext);
    }

    private static string? HttpsOrNull(string? url)
        => MediaRouter.TryParseHttpUrl(url, out _) ? url : null;

    private static string PrettySite(string? category) => category?.ToLowerInvariant() switch
    {
        "instagram" => "Instagram",
        "twitter" => "X",
        "threads" => "Threads",
        "reddit" => "Reddit",
        null or "" => "Gallery",
        var name => char.ToUpperInvariant(name[0]) + name[1..]
    };

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
