using System.Text;
using System.Text.RegularExpressions;

namespace FetchIt.Services;

/// <summary>
/// Small rolling log in %LocalAppData%\WasdFetchIt\logs. Keeps the raw tool
/// output that the UI collapses into short messages, so a failed link can be
/// diagnosed after the fact.
/// </summary>
public static class AppLog
{
    public const long MaxBytes = 1024 * 1024;
    public const int KeepFiles = 3;

    private static readonly object Gate = new();
    // Every credential-bearing form the app itself builds: cookie jar paths,
    // gallery-dl api-token, Instagram sessionid, GoFile bearer/cookie/query
    // tokens, password query values and URL fragments (GoFile folder passwords).
    private static readonly Regex Secrets = new(
        @"(--cookies\s+)(\S+)|(api-token=)(\S+)|(sessionid=)([^;\s]+)|(Authorization:\s*Bearer\s+)(\S+)|(accountToken=)([^;\s]+)|([?&]token=)([^&\s]+)|([?&]password=)([^&\s]*)|(#)(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "logs");

    public static string FilePath => Path.Combine(Directory, "fetchit.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null)
        => Write("ERROR", exception is null ? message : $"{message}\n{exception}");

    internal static string Redact(string text) => Secrets.Replace(text, match =>
    {
        for (var group = 1; group < match.Groups.Count; group += 2)
        {
            if (match.Groups[group].Success)
                return match.Groups[group].Value + "***";
        }

        return match.Value;
    });

    /// <summary>A URL safe to log: no fragment, no query (both can carry passwords or tokens).</summary>
    internal static string SafeUrl(string? url)
    {
        var text = (url ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return Redact(text);
        return uri.GetLeftPart(UriPartial.Path);
    }

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Redact(message).TrimEnd()}{Environment.NewLine}";
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                RotateIfNeeded();
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes)
            return;

        for (var n = KeepFiles - 1; n >= 1; n--)
        {
            var older = Rotated(n);
            var newer = n == 1 ? FilePath : Rotated(n - 1);
            if (File.Exists(older))
                File.Delete(older);
            if (File.Exists(newer))
                File.Move(newer, older);
        }
    }

    private static string Rotated(int n) => Path.Combine(Directory, $"fetchit.{n}.log");
}
