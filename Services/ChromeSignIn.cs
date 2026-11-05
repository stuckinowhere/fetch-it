using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FetchIt.Services;

public enum ChromeSignInStatus
{
    /// <summary>The user signed in; <see cref="ChromeSignInResult.Cookies"/> holds the session.</summary>
    SignedIn,

    /// <summary>The user closed Chrome, or the wait was cancelled.</summary>
    Closed,

    /// <summary>Chrome could not be driven; the caller may use another sign-in.</summary>
    Unavailable
}

public sealed record ChromeSignInResult(ChromeSignInStatus Status, IReadOnlyList<CookieRow> Cookies);

/// <summary>
/// Sign in with the user's real Chrome. Chrome's own cookie store is sealed by
/// App-Bound Encryption and remote debugging is refused on the default profile,
/// so this opens a throwaway profile, waits for Instagram's session cookie over
/// the DevTools protocol, then closes Chrome. Only instagram.com cookies leave it.
/// </summary>
public sealed class ChromeSignIn(string chromePath, string profileRoot, string startUrl)
{
    public const string LoginUrl = "https://www.instagram.com/accounts/login/";

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan StartupLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(5);

    private int _nextId;

    public static string? FindChrome(IEnumerable<string>? candidates = null)
        => (candidates ?? DefaultCandidates()).FirstOrDefault(File.Exists);

    private static IEnumerable<string> DefaultCandidates()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
        }
    }

    public async Task<ChromeSignInResult> RunAsync(CancellationToken cancellationToken)
    {
        // A new folder per run: nothing stale can pass for a fresh login, even
        // when an earlier profile could not be deleted.
        SessionCookies.ClearWebProfile(profileRoot);
        var profile = Path.Combine(profileRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        var chrome = Start(profile);
        if (chrome is null)
            return Done(ChromeSignInStatus.Unavailable);

        using (chrome)
        using (var socket = new ClientWebSocket())
        {
            try
            {
                var endpoint = await WaitForEndpointAsync(chrome, profile, cancellationToken).ConfigureAwait(false);
                if (endpoint is null)
                    return Done(chrome.HasExited ? ChromeSignInStatus.Closed : ChromeSignInStatus.Unavailable);

                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{endpoint.Value.Port}{endpoint.Value.Path}"), cancellationToken)
                    .ConfigureAwait(false);

                while (!chrome.HasExited)
                {
                    var json = await SendAsync(socket, "Storage.getCookies", cancellationToken).ConfigureAwait(false);
                    var cookies = InstagramCookies(json);
                    if (HasSession(cookies))
                        return new ChromeSignInResult(ChromeSignInStatus.SignedIn, cookies);
                    await Task.Delay(PollEvery, cancellationToken).ConfigureAwait(false);
                }

                return Done(ChromeSignInStatus.Closed);
            }
            catch (OperationCanceledException)
            {
                return Done(ChromeSignInStatus.Closed);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or InvalidOperationException)
            {
                // Closing Chrome drops the socket just before the process ends.
                // That is the user's answer, not a fault to route around.
                if (await ExitsSoonAsync(chrome).ConfigureAwait(false))
                    return Done(ChromeSignInStatus.Closed);
                AppLog.Warn($"Chrome sign-in failed: {ex.Message}");
                return Done(ChromeSignInStatus.Unavailable);
            }
            finally
            {
                await StopAsync(chrome, socket).ConfigureAwait(false);
                SessionCookies.ClearWebProfile(profileRoot);
            }
        }
    }

    private static ChromeSignInResult Done(ChromeSignInStatus status) => new(status, []);

    private static async Task<bool> ExitsSoonAsync(Process chrome)
    {
        using var limit = new CancellationTokenSource(ExitLimit);
        try
        {
            await chrome.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private Process? Start(string profile)
    {
        var start = new ProcessStartInfo(chromePath) { UseShellExecute = false };
        start.ArgumentList.Add($"--user-data-dir={profile}");
        start.ArgumentList.Add("--remote-debugging-port=0"); // A free port on 127.0.0.1.
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--disable-sync");
        start.ArgumentList.Add(startUrl);
        try
        {
            return Process.Start(start);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not start Chrome: {ex.Message}");
            return null;
        }
    }

    private static async Task<(int Port, string Path)?> WaitForEndpointAsync(
        Process chrome,
        string profile,
        CancellationToken cancellationToken)
    {
        var file = Path.Combine(profile, "DevToolsActivePort");
        var deadline = DateTime.UtcNow + StartupLimit;
        while (DateTime.UtcNow < deadline && !chrome.HasExited)
        {
            if (File.Exists(file))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                    if (ParseActivePort(text) is { } endpoint)
                        return endpoint;
                }
                catch (IOException)
                {
                    // Chrome is still writing it.
                }
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Line 1 is the port, line 2 the browser target path.</summary>
    internal static (int Port, string Path)? ParseActivePort(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2
            || !int.TryParse(lines[0], out var port)
            || port is < 1 or > 65535
            || !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
            return null;
        return (port, lines[1]);
    }

    /// <summary>Instagram cookies from a Storage.getCookies reply; everything else is dropped.</summary>
    internal static IReadOnlyList<CookieRow> InstagramCookies(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("cookies", out var cookies)
            || cookies.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<CookieRow>();
        foreach (var cookie in cookies.EnumerateArray())
        {
            var domain = Text(cookie, "domain");
            var host = domain.TrimStart('.');
            if (!host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase))
                continue;

            var session = cookie.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.True;
            var expires = !session && cookie.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number
                ? (long)Math.Max(0, e.GetDouble())
                : 0;
            var path = Text(cookie, "path");
            rows.Add(new CookieRow(
                domain,
                path.Length == 0 ? "/" : path,
                cookie.TryGetProperty("secure", out var secure) && secure.ValueKind == JsonValueKind.True,
                expires,
                Text(cookie, "name"),
                Text(cookie, "value")));
        }

        return rows;
    }

    internal static bool HasSession(IEnumerable<CookieRow> cookies)
        => cookies.Any(row => row.Name == "sessionid" && !string.IsNullOrWhiteSpace(row.Value));

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private async Task<string> SendAsync(ClientWebSocket socket, string method, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method });
        await socket.SendAsync(request, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw new IOException("Chrome closed the connection.");
                message.Write(buffer, 0, received.Count);
                if (message.Length > 16 * 1024 * 1024)
                    throw new IOException("Chrome reply is too large.");
            }
            while (!received.EndOfMessage);

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("id", out var replyId)
                && replyId.ValueKind == JsonValueKind.Number
                && replyId.GetInt32() == id)
                return text;
        }
    }

    private async Task StopAsync(Process chrome, ClientWebSocket socket)
    {
        try
        {
            if (chrome.HasExited)
                return;
            if (socket.State == WebSocketState.Open)
            {
                var request = JsonSerializer.SerializeToUtf8Bytes(new { id = Interlocked.Increment(ref _nextId), method = "Browser.close" });
                using var sendLimit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.SendAsync(request, WebSocketMessageType.Text, true, sendLimit.Token).ConfigureAwait(false);
            }

            using var exitLimit = new CancellationTokenSource(ExitLimit);
            await chrome.WaitForExitAsync(exitLimit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException)
        {
            try
            {
                chrome.Kill(entireProcessTree: true);
            }
            catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                AppLog.Warn($"Could not close Chrome: {killError.Message}");
            }
        }
    }
}
