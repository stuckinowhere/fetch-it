using System.Text.RegularExpressions;
using FetchIt.Models;

namespace FetchIt.Services;

public static class MediaRouter
{
    private static readonly Regex HttpUrl = new(
        @"^https?://[^\s]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParseHttpUrl(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (!HttpUrl.IsMatch(trimmed))
            return false;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out uri!)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public static EngineKind Prefer(Uri uri)
        => IsSocialPostHost(uri) ? EngineKind.GalleryDl : EngineKind.YtDlp;

    public static bool NeedsLoginRow(Uri uri) => IsSocialPostHost(uri);

    public static bool IsSocialPostHost(Uri uri)
    {
        var host = uri.Host.Trim().ToLowerInvariant();
        if (host.StartsWith("www."))
            host = host[4..];

        return host is "instagram.com" or "instagr.am"
            or "threads.net" or "threads.com"
            or "twitter.com" or "x.com"
            or "mobile.twitter.com" or "mobile.x.com";
    }

    public static string SanitizeFolderName(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "fetch";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(ch => invalid.Contains(ch) ? ' ' : ch).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length > 80)
            cleaned = cleaned[..80].Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "fetch" : cleaned;
    }
}
