using FetchIt.Models;

namespace FetchIt.Services;

public static class SaveClash
{
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".webm", ".m4a", ".mov"];

    public static IReadOnlyList<string> PlannedNames(MediaProbe probe)
    {
        if (probe.Engine == EngineKind.YtDlp)
        {
            var stem = MediaRouter.SanitizeFolderName(probe.Title);
            var count = Math.Max(1, probe.Items.Count > 0 ? probe.Items.Count : probe.FileCount);
            if (count <= 1)
                return [$"{stem}.mp4"];

            var numbered = new List<string>(count);
            for (var i = 1; i <= count; i++)
                numbered.Add($"{stem}_{i:D3}.mp4");
            return numbered;
        }

        var title = MediaRouter.SanitizeFolderName(probe.Title);
        var names = new List<string>(probe.Items.Count);
        var index = 1;
        foreach (var item in probe.Items)
        {
            names.Add($"{title}_{index}.{Extension(item)}");
            index++;
        }

        return names;
    }

    public static IReadOnlyList<string> ExistingNames(string folder, MediaProbe probe)
    {
        var dest = Path.GetFullPath(folder);
        if (!Directory.Exists(dest))
            return [];

        var found = new List<string>();
        foreach (var name in PlannedNames(probe))
        {
            if (File.Exists(Path.Combine(dest, name)))
                found.Add(name);
        }

        if (found.Count > 0 || probe.Engine != EngineKind.YtDlp)
            return found;

        var stem = Path.GetFileNameWithoutExtension(PlannedNames(probe)[0]);
        foreach (var ext in VideoExtensions)
        {
            var name = stem + ext;
            if (File.Exists(Path.Combine(dest, name)))
                found.Add(name);
        }

        return found;
    }

    public static string UniquePath(string dest, string fileName)
    {
        var path = Path.Combine(dest, fileName);
        if (!File.Exists(path))
            return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; n < 1000; n++)
        {
            path = Path.Combine(dest, $"{stem}_{n}{ext}");
            if (!File.Exists(path))
                return path;
        }

        return Path.Combine(dest, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    public static string Extension(MediaItem item)
    {
        if (item.DownloadUrl is not null
            && Uri.TryCreate(item.DownloadUrl, UriKind.Absolute, out var uri))
        {
            var ext = Path.GetExtension(uri.AbsolutePath).Trim('.');
            if (ext.Length is > 0 and < 8)
                return ext.ToLowerInvariant();
        }

        return item.Kind == MediaKind.Video ? "mp4" : "jpg";
    }
}
