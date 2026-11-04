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
            File.WriteAllText(path, NetscapeCookies.ToText(
            [
                new CookieRow(".instagram.com", "/", true, 0, "sessionid", "abc"),
                new CookieRow(".instagram.com", "/", true, 0, "ds_user_id", "1")
            ]));
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
    public void Evaluate_reads_state_and_expiry_from_the_sessionid_row()
    {
        var now = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        string Text(long expires) => NetscapeCookies.ToText(
        [
            new CookieRow(".instagram.com", "/", true, now.AddDays(-400).ToUnixTimeSeconds(), "old_thing", "x"),
            new CookieRow(".instagram.com", "/", true, expires, "sessionid", "abc")
        ]);

        Assert.Equal(SessionState.None, SessionCookies.Evaluate(null, now).State);
        Assert.Equal(SessionState.None, SessionCookies.Evaluate("# Netscape HTTP Cookie File", now).State);

        var session = SessionCookies.Evaluate(Text(0), now);
        Assert.Equal(SessionState.Active, session.State);
        Assert.Null(session.Expires);

        var future = now.AddDays(90).ToUnixTimeSeconds();
        var active = SessionCookies.Evaluate(Text(future), now);
        Assert.Equal(SessionState.Active, active.State);
        Assert.Equal(future, active.Expires!.Value.ToUnixTimeSeconds());

        Assert.Equal(SessionState.Expired, SessionCookies.Evaluate(Text(now.AddDays(-1).ToUnixTimeSeconds()), now).State);
    }

    [Fact]
    public void ClearWebProfile_removes_the_folder_and_tolerates_a_missing_one()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fetchit-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Default"));
        File.WriteAllText(Path.Combine(folder, "Default", "Cookies"), "x");

        SessionCookies.ClearWebProfile(folder);
        Assert.False(Directory.Exists(folder));
        SessionCookies.ClearWebProfile(folder);
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
