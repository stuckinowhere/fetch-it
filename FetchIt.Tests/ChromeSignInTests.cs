using System.Net.WebSockets;
using System.Text;
using FetchIt.Services;

namespace FetchIt.Tests;

public class ChromeSignInTests
{
    [Theory]
    [InlineData("54321\n/devtools/browser/abc-123\n", 54321, "/devtools/browser/abc-123")]
    [InlineData("9222\r\n/devtools/browser/x\r\n", 9222, "/devtools/browser/x")]
    public void ParseActivePort_reads_port_and_path(string text, int port, string path)
        => Assert.Equal((port, path), ChromeSignIn.ParseActivePort(text));

    [Theory]
    [InlineData("")]
    [InlineData("54321\n")]
    [InlineData("abc\n/devtools/browser/x")]
    [InlineData("0\n/devtools/browser/x")]
    [InlineData("70000\n/devtools/browser/x")]
    [InlineData("9222\n/json/version")]
    public void ParseActivePort_rejects_anything_else(string text)
        => Assert.Null(ChromeSignIn.ParseActivePort(text));

    [Fact]
    public void InstagramCookies_keeps_only_instagram_and_maps_fields()
    {
        const string json = """
            {"id":3,"result":{"cookies":[
              {"name":"sessionid","value":"abc","domain":".instagram.com","path":"/","expires":1800000000.5,"session":false,"secure":true},
              {"name":"csrftoken","value":"t","domain":"www.instagram.com","path":"","expires":-1,"session":true,"secure":false},
              {"name":"SID","value":"google-secret","domain":".google.com","path":"/","expires":1800000000,"session":false,"secure":true},
              {"name":"sessionid","value":"evil","domain":".notinstagram.com","path":"/","expires":1800000000,"session":false,"secure":true},
              {"name":"sessionid","value":"evil","domain":"instagram.com.evil.example","path":"/","expires":1800000000,"session":false,"secure":true}
            ]}}
            """;
        var rows = ChromeSignIn.InstagramCookies(json);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new CookieRow(".instagram.com", "/", true, 1800000000, "sessionid", "abc"), rows[0]);
        Assert.Equal(new CookieRow("www.instagram.com", "/", false, 0, "csrftoken", "t"), rows[1]);
        Assert.True(ChromeSignIn.HasSession(rows));
    }

    [Theory]
    [InlineData("""{"id":1,"result":{"cookies":[]}}""")]
    [InlineData("""{"id":1,"error":{"message":"nope"}}""")]
    [InlineData("""{"id":1,"result":{"cookies":[{"name":"sessionid","value":"","domain":".instagram.com"}]}}""")]
    [InlineData("""{"id":1,"result":{"cookies":[{"name":"csrftoken","value":"t","domain":".instagram.com"}]}}""")]
    public void No_session_until_sessionid_has_a_value(string json)
        => Assert.False(ChromeSignIn.HasSession(ChromeSignIn.InstagramCookies(json)));

    [Fact]
    public void FindChrome_returns_the_first_file_that_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), "fetchit-chrome-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, [0]);
        try
        {
            Assert.Equal(path, ChromeSignIn.FindChrome([path + ".missing", path]));
            Assert.Null(ChromeSignIn.FindChrome([path + ".missing"]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_chrome_is_unavailable_so_the_built_in_window_can_take_over()
    {
        var root = Path.Combine(Path.GetTempPath(), "fetchit-chrome-" + Guid.NewGuid().ToString("N"));
        var result = await new ChromeSignIn(Path.Combine(root, "missing.exe"), root, "about:blank")
            .RunAsync(CancellationToken.None);
        Assert.Equal(ChromeSignInStatus.Unavailable, result.Status);
        SessionCookies.ClearWebProfile(root);
    }

    // Real Chrome, throwaway profile, about:blank: nothing leaves the machine.
    private static bool RealChromeEnabled => Environment.GetEnvironmentVariable("FETCHIT_E2E") == "1";

    [Fact]
    public async Task Real_chrome_detects_a_session_cookie_then_closes_and_cleans_up()
    {
        if (!RealChromeEnabled || ChromeSignIn.FindChrome() is not { } chrome)
            return;

        var root = Path.Combine(Path.GetTempPath(), "fetchit-chrome-" + Guid.NewGuid().ToString("N"));
        var run = new ChromeSignIn(chrome, root, "about:blank").RunAsync(CancellationToken.None);

        var (socket, _) = await ConnectAsync(root);
        using (socket)
        {
            await SendAsync(socket, """
                {"id":900,"method":"Storage.setCookies","params":{"cookies":[
                  {"name":"sessionid","value":"dummy-local-only","domain":".instagram.com","path":"/","secure":true},
                  {"name":"other","value":"x","domain":".example.com","path":"/"}
                ]}}
                """);
        }

        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ChromeSignInStatus.SignedIn, result.Status);
        var row = Assert.Single(result.Cookies);
        Assert.Equal("dummy-local-only", row.Value);
        SessionCookies.ClearWebProfile(root);
    }

    [Fact]
    public async Task Real_chrome_closed_by_the_user_is_reported_as_closed()
    {
        if (!RealChromeEnabled || ChromeSignIn.FindChrome() is not { } chrome)
            return;

        var root = Path.Combine(Path.GetTempPath(), "fetchit-chrome-" + Guid.NewGuid().ToString("N"));
        var run = new ChromeSignIn(chrome, root, "about:blank").RunAsync(CancellationToken.None);

        var (socket, _) = await ConnectAsync(root);
        using (socket)
            await SendAsync(socket, """{"id":901,"method":"Browser.close"}""");

        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ChromeSignInStatus.Closed, result.Status);
        Assert.Empty(result.Cookies);
        SessionCookies.ClearWebProfile(root);
    }

    [Fact]
    public async Task Real_chrome_is_closed_when_the_sign_in_is_cancelled()
    {
        if (!RealChromeEnabled || ChromeSignIn.FindChrome() is not { } chrome)
            return;

        var root = Path.Combine(Path.GetTempPath(), "fetchit-chrome-" + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        var run = new ChromeSignIn(chrome, root, "about:blank").RunAsync(cancel.Token);

        var (socket, file) = await ConnectAsync(root);
        socket.Dispose();
        cancel.Cancel();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ChromeSignInStatus.Closed, result.Status);
        Assert.False(File.Exists(file));
    }

    private static async Task<(ClientWebSocket Socket, string File)> ConnectAsync(string root)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var file = Directory.Exists(root)
                ? Directory.GetFiles(root, "DevToolsActivePort", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (file is not null)
            {
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(file);
                }
                catch (IOException)
                {
                    text = "";
                }

                if (ChromeSignIn.ParseActivePort(text) is { } endpoint)
                {
                    var socket = new ClientWebSocket();
                    await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{endpoint.Port}{endpoint.Path}"), CancellationToken.None);
                    return (socket, file);
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Chrome did not expose DevTools.");
    }

    private static async Task SendAsync(ClientWebSocket socket, string json)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4096];
        try
        {
            await socket.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Browser.close drops the socket before it answers.
        }
    }
}
