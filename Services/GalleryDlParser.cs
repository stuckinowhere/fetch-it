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
        "mp4", "webm", "mkv", "mov", "m4v", "avi"
    };

    public static MediaProbe Parse(string json, bool needsLogin = true)
    {
        var items = ParseItems(json);
        var title = items.Select(item => item.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "post";
        var site = items.Select(item => item.Site).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "Gallery";
        var videos = items.Count(item => item.IsVideo);
        var images = items.Count(item => !item.IsVideo);

        if (videos == 0 && images == 0)
            images = Math.Max(1, items.Count);

        return new MediaProbe
        {
            Title = title,
            Site = site,
            VideoCount = videos,
            ImageCount = images,
            NeedsLogin = needsLogin,
            Engine = EngineKind.GalleryDl
        };
    }

    internal static IReadOnlyList<(string Title, string Site, bool IsVideo)> ParseItems(string json)
    {
        var found = new List<(string, string, bool)>();
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

    private static void AddArray(JsonElement array, List<(string, string, bool)> found)
    {
        if (array.GetArrayLength() == 2
            && array[0].ValueKind == JsonValueKind.String
            && array[1].ValueKind == JsonValueKind.Object)
        {
            TryAdd(array[1], found);
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

    private static void TryAdd(JsonElement el, List<(string, string, bool)> found)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return;

        var payload = el;

        var ext = ReadString(payload, "extension") ?? ReadString(payload, "ext") ?? "";
        var title = ReadString(payload, "title")
                    ?? ReadString(payload, "description")
                    ?? ReadString(payload, "filename")
                    ?? "post";
        var site = PrettySite(ReadString(payload, "category") ?? ReadString(payload, "subcategory"));
        found.Add((title, site, IsVideo(ext)));
    }

    private static bool IsVideo(string ext)
    {
        ext = ext.Trim().Trim('.');
        if (VideoExt.Contains(ext))
            return true;
        return !ImageExt.Contains(ext) && VideoExt.Contains(ext);
    }

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
