using System.Text.RegularExpressions;
using FetchIt.Models;

namespace FetchIt.Services;

public static class MediaRouter
{
    private static readonly Regex HttpsUrl = new(
        @"^https://[^\s]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool TryParseHttpUrl(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (!HttpsUrl.IsMatch(trimmed))
            return false;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out uri!)
               && uri.Scheme == Uri.UriSchemeHttps;
    }

    public static EngineKind Prefer(Uri uri)
        => IsGalleryHost(uri) ? EngineKind.GalleryDl : EngineKind.YtDlp;

    public static string PublicOnlyMessage => "Only public profiles.";
    public static string InstagramSessionMessage =>
        "Instagram hid the posts. Sign in when asked. Chrome can stay open.";

    public static bool IsInstagram(Uri uri) => IsInstagramHost(uri);

    public static string CanonicalPublicUrl(string url)
    {
        if (!TryParseHttpUrl(url, out var uri))
            return url.Trim();

        if (!IsInstagramHost(uri))
        {
            if (IsOkRuHost(uri))
                return CanonicalOkRu(uri);
            return uri.ToString();
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && parts[0].Equals("stories", StringComparison.OrdinalIgnoreCase)
            && !parts[1].Equals("highlights", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{uri.Host}/{parts[1]}/posts/";
        }

        if (parts.Length == 1)
            return $"{uri.Scheme}://{uri.Host}/{parts[0]}/posts/";

        return uri.ToString();
    }

    public static bool LooksPrivate(string text)
        => text.Contains("This account is private", StringComparison.OrdinalIgnoreCase)
           || text.Contains("private account", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeMissingSession(string text)
        => text.Contains("401", StringComparison.Ordinal)
           || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
           || text.Contains("login page", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
           || text.Contains("unable to open database", StringComparison.OrdinalIgnoreCase)
           || text.Contains("cookies", StringComparison.OrdinalIgnoreCase) && text.Contains("denied", StringComparison.OrdinalIgnoreCase);

    public static bool IsSocialPostHost(Uri uri) => IsGalleryHost(uri);

    public static bool IsGalleryHost(Uri uri)
    {
        var host = NormalizedHost(uri);
        return host is "instagram.com" or "instagr.am"
            or "threads.net" or "threads.com";
    }

    private static bool IsOkRuHost(Uri uri)
    {
        var host = NormalizedHost(uri);
        return host is "ok.ru" or "odnoklassniki.ru"
            or "m.ok.ru" or "mobile.ok.ru"
            or "m.odnoklassniki.ru";
    }

    private static string CanonicalOkRu(Uri uri)
    {
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && parts[0] is "video" or "videoembed" or "live")
        {
            var id = parts[1].Split('?')[0];
            if (id.Length > 0)
                return $"https://ok.ru/video/{id}";
        }

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2
                && Uri.UnescapeDataString(kv[0]).Equals("st.mvId", StringComparison.OrdinalIgnoreCase)
                && kv[1].Length > 0)
            {
                return $"https://ok.ru/video/{Uri.UnescapeDataString(kv[1])}";
            }
        }

        return $"https://ok.ru{uri.PathAndQuery}";
    }

    private static bool IsInstagramHost(Uri uri)
    {
        var host = NormalizedHost(uri);
        return host is "instagram.com" or "instagr.am";
    }

    private static string NormalizedHost(Uri uri)
    {
        var host = uri.Host.Trim().ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    public static string SanitizeFolderName(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "fetch";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(ch => invalid.Contains(ch) ? ' ' : ch).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim().TrimEnd('.');
        cleaned = cleaned.Replace("..", "", StringComparison.Ordinal);
        if (cleaned is "." or ".." || string.IsNullOrWhiteSpace(cleaned))
            return "fetch";
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(stem) || ReservedNames.Contains(cleaned))
            return "fetch";
        if (cleaned.Length > 80)
            cleaned = cleaned[..80].Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "fetch" : cleaned;
    }

    public static string SafeCombine(string folder, string title)
    {
        var root = Path.GetFullPath(folder);
        var dest = Path.GetFullPath(Path.Combine(root, SanitizeFolderName(title)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!dest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(dest, root, StringComparison.OrdinalIgnoreCase))
            return root;
        return dest;
    }
}
