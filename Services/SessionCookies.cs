using System.Security.Cryptography;
using System.Text;

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

    public static string ToText(IEnumerable<CookieRow> cookies)
    {
        using var writer = new StringWriter();
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

        return writer.ToString();
    }
}

public sealed class ToolCookieSession : IDisposable
{
    public IReadOnlyList<string> Arguments { get; }
    private readonly string? _tempFile;

    public ToolCookieSession(IReadOnlyList<string> arguments, string? tempFile = null)
    {
        Arguments = arguments;
        _tempFile = tempFile;
    }

    public void Dispose()
    {
        if (string.IsNullOrEmpty(_tempFile))
            return;
        try
        {
            if (File.Exists(_tempFile))
                File.Delete(_tempFile);
        }
        catch
        {
        }
    }
}

public static class SessionCookies
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WasdFetchIt/instagram-cookies/v1");

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "instagram.cookies.bin");

    public static string LegacyFilePath => Path.Combine(
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
            MigrateLegacy();
            var text = ReadPlaintext();
            return text is not null && LooksLikeSession(text);
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
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var plain = Encoding.UTF8.GetBytes(NetscapeCookies.ToText(cookies));
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, protectedBytes);
        TryDelete(LegacyFilePath);
    }

    public static void Clear()
    {
        TryDelete(FilePath);
        TryDelete(LegacyFilePath);
    }

    public static ToolCookieSession BindForTool(bool socialHost)
    {
        if (!socialHost)
            return new ToolCookieSession([]);

        if (HasUsableFile())
        {
            var text = ReadPlaintext();
            if (text is not null)
            {
                var temp = Path.Combine(
                    Path.GetTempPath(),
                    "fetchit-ig-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(temp, text);
                return new ToolCookieSession(["--cookies", temp], temp);
            }
        }

        if (ChromeCookieDb.IsReadable())
            return new ToolCookieSession(["--cookies-from-browser", "chrome"]);

        return new ToolCookieSession([]);
    }

    private static void MigrateLegacy()
    {
        if (File.Exists(FilePath) || !File.Exists(LegacyFilePath))
            return;
        try
        {
            var text = File.ReadAllText(LegacyFilePath);
            if (!LooksLikeSession(text))
                return;
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(text),
                Entropy,
                DataProtectionScope.CurrentUser);
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(FilePath, protectedBytes);
            TryDelete(LegacyFilePath);
        }
        catch
        {
            // keep the legacy file if protect fails
        }
    }

    private static string? ReadPlaintext()
    {
        if (!File.Exists(FilePath))
            return null;
        var protectedBytes = File.ReadAllBytes(FilePath);
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
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
}
