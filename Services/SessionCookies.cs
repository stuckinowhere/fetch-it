namespace FetchIt.Services;

public readonly record struct CookieRow(
    string Domain,
    string Path,
    bool Secure,
    long ExpiresUnix,
    string Name,
    string Value);

public static class NetscapeCookies
{
    public static void Write(string path, IEnumerable<CookieRow> cookies)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(path);
        writer.WriteLine("# Netscape HTTP Cookie File");
        foreach (var cookie in cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Name))
                continue;
            var domain = string.IsNullOrWhiteSpace(cookie.Domain) ? ".instagram.com" : cookie.Domain;
            var flag = domain.StartsWith('.') ? "TRUE" : "FALSE";
            var pathPart = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path;
            var secure = cookie.Secure ? "TRUE" : "FALSE";
            var expires = cookie.ExpiresUnix < 0 ? 0 : cookie.ExpiresUnix;
            writer.WriteLine($"{domain}\t{flag}\t{pathPart}\t{secure}\t{expires}\t{cookie.Name}\t{cookie.Value}");
        }
    }
}

public static class SessionCookies
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "instagram.cookies.txt");

    public static string WebViewUserData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "ig-webview");

    public static bool HasUsableFile()
    {
        try
        {
            return File.Exists(FilePath) && LooksLikeSession(File.ReadAllText(FilePath));
        }
        catch
        {
            return false;
        }
    }

    internal static bool LooksLikeSession(string text)
        => text.Contains("sessionid", StringComparison.Ordinal)
           && text.Contains("instagram", StringComparison.OrdinalIgnoreCase);

    public static void Save(IEnumerable<CookieRow> cookies)
        => NetscapeCookies.Write(FilePath, cookies);

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch
        {
            // ignore
        }
    }

    public static IReadOnlyList<string> GalleryDlArguments()
        => GalleryDlArguments(HasUsableFile(), FilePath);

    internal static IReadOnlyList<string> GalleryDlArguments(bool hasSession, string filePath)
        => hasSession
            ? ["--cookies", filePath]
            : ["--cookies-from-browser", "chrome"];
}

public static class ChromeCookieDb
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Google",
        "Chrome",
        "User Data",
        "Default",
        "Network",
        "Cookies");

    public static bool IsReadable()
    {
        try
        {
            if (!File.Exists(Path))
                return false;
            using var stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> YtDlpArguments()
        => YtDlpArguments(IsReadable());

    internal static IReadOnlyList<string> YtDlpArguments(bool chromeReadable)
        => chromeReadable ? ["--cookies-from-browser", "chrome"] : [];
}
