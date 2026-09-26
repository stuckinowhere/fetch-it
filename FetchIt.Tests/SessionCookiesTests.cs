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
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Save_roundtrip_uses_dpapi_and_temp_cookie_file()
    {
        var bin = SessionCookies.FilePath;
        var legacy = SessionCookies.LegacyFilePath;
        byte[]? binBackup = File.Exists(bin) ? File.ReadAllBytes(bin) : null;
        string? legacyBackup = File.Exists(legacy) ? File.ReadAllText(legacy) : null;
        try
        {
            SessionCookies.Clear();
            SessionCookies.Save(
            [
                new CookieRow(".instagram.com", "/", true, 0, "sessionid", "abc"),
                new CookieRow(".instagram.com", "/", true, 0, "ds_user_id", "1")
            ]);
            Assert.True(File.Exists(bin));
            var raw = File.ReadAllText(bin);
            Assert.DoesNotContain("sessionid", raw, StringComparison.Ordinal);
            Assert.True(SessionCookies.HasUsableFile());

            using var bound = SessionCookies.BindForTool(socialHost: true);
            Assert.Equal("--cookies", bound.Arguments[0]);
            var temp = bound.Arguments[1];
            Assert.True(File.Exists(temp));
            Assert.Contains("sessionid", File.ReadAllText(temp), StringComparison.Ordinal);
            using var skipped = SessionCookies.BindForTool(socialHost: false);
            Assert.Empty(skipped.Arguments);
            bound.Dispose();
            Assert.False(File.Exists(temp));
        }
        finally
        {
            SessionCookies.Clear();
            if (binBackup is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
                File.WriteAllBytes(bin, binBackup);
            }
            if (legacyBackup is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
                File.WriteAllText(legacy, legacyBackup);
            }
        }
    }
}
