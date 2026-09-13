using System.Text.RegularExpressions;

namespace FetchIt.Services;

public static class ThumbnailUrl
{
    private static readonly Regex TwitterSizeSuffix = new(
        ":(orig|large|medium|small|thumb|4096x4096)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "pbs.twimg.com", "video.twimg.com", "twimg.com",
        "i.ytimg.com", "i1.ytimg.com", "i2.ytimg.com", "i3.ytimg.com", "i4.ytimg.com",
        "preview.redd.it", "i.redd.it", "external-preview.redd.it"
    };

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "webp", "gif", "bmp"
    };

    public static bool LooksLikeImage(string? url)
    {
        if (!TryHttps(url, out var uri))
            return false;
        var host = Host(uri);
        if (host is "pic.twitter.com" or "t.co" or "x.com" or "twitter.com"
            or "mobile.x.com" or "mobile.twitter.com")
            return false;
        if (ImageHosts.Contains(host))
            return true;
        if (host.Contains("cdninstagram", StringComparison.OrdinalIgnoreCase)
            || host.Contains("fbcdn", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("scontent", StringComparison.OrdinalIgnoreCase)
            || host.Contains("instagram", StringComparison.OrdinalIgnoreCase))
            return true;
        var ext = Path.GetExtension(TwitterSizeSuffix.Replace(uri.AbsolutePath, "")).Trim('.');
        return ImageExt.Contains(ext);
    }

    public static string? Pick(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (LooksLikeImage(candidate) && TryHttps(candidate, out var uri))
                return uri.ToString();
        }

        return null;
    }

    public static IReadOnlyList<string> Candidates(string url)
    {
        var list = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || list.Contains(value, StringComparer.OrdinalIgnoreCase))
                return;
            list.Add(value);
        }

        Add(ForPreview(url));
        Add(TryHttps(url, out var uri) ? uri.ToString() : url);
        return list;
    }

    public static string ForPreview(string url)
    {
        if (!TryHttps(url, out var uri))
            return url;

        var host = Host(uri);
        if (host is "pbs.twimg.com" or "video.twimg.com" or "twimg.com")
            return TwitterPreview(uri, host);

        return uri.ToString();
    }

    public static string ForSave(string url)
    {
        if (!TryHttps(url, out var uri))
            return url;

        var host = Host(uri);
        if (host is "pbs.twimg.com" or "twimg.com")
            return TwitterSave(uri, host);

        return uri.ToString();
    }

    public static Uri? RefererFor(string url)
    {
        if (!TryHttps(url, out var uri))
            return null;
        var host = Host(uri);
        if (host is "pbs.twimg.com" or "video.twimg.com" or "twimg.com"
            or "x.com" or "twitter.com" or "mobile.twitter.com" or "mobile.x.com")
            return new Uri("https://x.com/");
        if (host.Contains("cdninstagram", StringComparison.OrdinalIgnoreCase)
            || host.Contains("fbcdn", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("scontent", StringComparison.OrdinalIgnoreCase)
            || host.Contains("instagram", StringComparison.OrdinalIgnoreCase))
            return new Uri("https://www.instagram.com/");
        return null;
    }

    public static bool NeedsXReferer(string url)
        => RefererFor(url)?.Host == "x.com";

    internal static int ScoreThumbWidth(int width)
    {
        if (width <= 0)
            return 0;
        if (width is >= 240 and <= 1280)
            return 2000 - Math.Abs(720 - width);
        if (width < 240)
            return width;
        return 400 - Math.Min(width / 20, 399);
    }

    private static string TwitterPreview(Uri uri, string host)
    {
        var path = TwitterSizeSuffix.Replace(uri.AbsolutePath, "");
        if (path.Contains("/media/", StringComparison.OrdinalIgnoreCase))
        {
            var file = Path.GetFileNameWithoutExtension(path);
            var dir = path[..path.LastIndexOf('/')];
            return $"https://{host}{dir}/{file}?format=jpg&name=small";
        }

        var query = uri.Query;
        if (query.Contains("name=", StringComparison.OrdinalIgnoreCase))
        {
            query = Regex.Replace(
                query,
                @"name=(orig|large|medium|thumb|4096x4096|2048x2048|900x900)",
                "name=small",
                RegexOptions.IgnoreCase);
            if (!query.Contains("format=", StringComparison.OrdinalIgnoreCase))
                query += "&format=jpg";
            return $"https://{host}{path}{query}";
        }

        return $"https://{host}{path}{uri.Query}";
    }

    private static string TwitterSave(Uri uri, string host)
    {
        var path = TwitterSizeSuffix.Replace(uri.AbsolutePath, "");
        if (!path.Contains("/media/", StringComparison.OrdinalIgnoreCase))
            return uri.ToString();

        var file = Path.GetFileNameWithoutExtension(path);
        var dir = path[..path.LastIndexOf('/')];
        var ext = Path.GetExtension(path).Trim('.').ToLowerInvariant();
        if (ext is not ("png" or "webp" or "gif"))
            ext = "jpg";
        return $"https://{host}{dir}/{file}?format={ext}&name=orig";
    }

    private static bool TryHttps(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var text = url.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal))
            text = "https:" + text;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(text, UriKind.Absolute, out var http)
            && LooksSafeHost(Host(http)))
            text = "https://" + http.Host + http.PathAndQuery;

        return Uri.TryCreate(text, UriKind.Absolute, out uri!)
               && uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool LooksSafeHost(string host)
        => ImageHosts.Contains(host)
           || host.Contains("cdninstagram", StringComparison.OrdinalIgnoreCase)
           || host.Contains("fbcdn", StringComparison.OrdinalIgnoreCase)
           || host.StartsWith("scontent", StringComparison.OrdinalIgnoreCase);

    private static string Host(Uri uri)
    {
        var host = uri.Host.Trim().ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }
}
