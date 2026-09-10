using FetchIt.Services;

namespace FetchIt.Tests;

public class SessionCookiesTests
{
    [Fact]
    public void Netscape_file_marks_instagram_session()
    {
        var path = Path.Combine(Path.GetTempPath(), "fetchit-cookies-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            NetscapeCookies.Write(path,
            [
                new CookieRow(".instagram.com", "/", true, 0, "sessionid", "abc"),
                new CookieRow(".instagram.com", "/", true, 0, "ds_user_id", "1")
            ]);
            var text = File.ReadAllText(path);
            Assert.True(SessionCookies.LooksLikeSession(text));
            Assert.Contains(".instagram.com\tTRUE\t/\tTRUE\t0\tsessionid\tabc", text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void GalleryDl_uses_cookie_file_when_session_exists()
    {
        Assert.Equal(
            ["--cookies", @"C:\tmp\instagram.cookies.txt"],
            SessionCookies.GalleryDlArguments(true, @"C:\tmp\instagram.cookies.txt"));
        Assert.Equal(
            ["--cookies-from-browser", "chrome"],
            SessionCookies.GalleryDlArguments(false, @"C:\tmp\instagram.cookies.txt"));
        Assert.Equal(["--cookies-from-browser", "chrome"], ChromeCookieDb.YtDlpArguments(true));
        Assert.Empty(ChromeCookieDb.YtDlpArguments(false));
    }
}
