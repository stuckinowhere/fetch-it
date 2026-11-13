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

public enum SessionState
{
    None,
    Active,
    Expired
}

public readonly record struct SessionStatus(SessionState State, DateTimeOffset? Expires);

public static class NetscapeCookies
{
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

    /// <summary>Throwaway Chrome profiles for sign-in; never the user's own.</summary>
    public static string ChromeProfile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "chrome-profile");

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
            return Evaluate(text, DateTimeOffset.UtcNow).State == SessionState.Active;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The saved session as shown in the Account window.</summary>
    public static SessionStatus GetStatus()
    {
        try
        {
            MigrateLegacy();
            return Evaluate(ReadPlaintext(), DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            // Shows as signed out, so the reason has to be somewhere.
            AppLog.Warn($"could not read the saved session: {ex.GetType().Name}");
            return new SessionStatus(SessionState.None, null);
        }
    }

    internal static SessionStatus Evaluate(string? text, DateTimeOffset now)
    {
        if (text is null || !LooksLikeSession(text))
            return new SessionStatus(SessionState.None, null);

        foreach (var line in text.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 7
                || !fields[5].Equals("sessionid", StringComparison.Ordinal)
                || !long.TryParse(fields[4], out var unix))
                continue;
            if (unix <= 0)
                return new SessionStatus(SessionState.Active, null);
            var expires = DateTimeOffset.FromUnixTimeSeconds(unix);
            return new SessionStatus(expires > now ? SessionState.Active : SessionState.Expired, expires);
        }

        return new SessionStatus(SessionState.None, null);
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

    /// <summary>Forget the saved session and the sign-in window's browser profile.</summary>
    public static void SignOut()
    {
        Clear();
        ClearWebProfile(WebViewUserData);
        ClearWebProfile(ChromeProfile);
    }

    internal static void ClearWebProfile(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // WebView2 can hold files briefly after its window closes. The next
            // sign-in window deletes its cookies before it loads anything.
            AppLog.Warn($"Could not remove the sign-in profile: {ex.Message}");
        }
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
