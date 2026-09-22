using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.Tests;

public class MediaRouterTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9wgXcQ", true, "YtDlp")]
    [InlineData("https://youtu.be/dQw4w9wgXcQ", true, "YtDlp")]
    [InlineData("https://www.tiktok.com/@x/video/1", true, "YtDlp")]
    [InlineData("https://vimeo.com/123", true, "YtDlp")]
    [InlineData("https://ok.ru/video/20079905452", true, "YtDlp")]
    [InlineData("https://www.ok.ru/videoembed/20648036891", true, "YtDlp")]
    [InlineData("https://www.dailymotion.com/video/x7tgad0", true, "YtDlp")]
    [InlineData("https://www.reddit.com/r/videos/comments/abc/title/", true, "YtDlp")]
    [InlineData("https://www.facebook.com/watch/?v=1", true, "YtDlp")]
    [InlineData("https://gofile.io/d/Soimwa", true, "YtDlp")]
    [InlineData("https://x.com/user/status/1", true, "GalleryDl")]
    [InlineData("https://twitter.com/user/status/1", true, "GalleryDl")]
    [InlineData("https://mobile.twitter.com/user/status/1", true, "GalleryDl")]
    [InlineData("https://instagram.com/p/abc", true, "GalleryDl")]
    [InlineData("https://www.instagram.com/stories/x/1", true, "GalleryDl")]
    [InlineData("https://www.threads.net/@x/post/1", true, "GalleryDl")]
    [InlineData("https://bunkr.cr/v/clip", true, "GalleryDl")]
    [InlineData("https://bunkr.si/a/album1", true, "GalleryDl")]
    [InlineData("https://bunkr.cr/", true, "GalleryDl")]
    [InlineData("not a url", false, "YtDlp")]
    [InlineData("ftp://example.com/a", false, "YtDlp")]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9wgXcQ", false, "YtDlp")]
    public void RoutesHosts(string text, bool valid, string engine)
    {
        var parsed = MediaRouter.TryParseHttpUrl(text, out var uri);
        Assert.Equal(valid, parsed);
        if (!valid)
            return;

        Assert.Equal(Enum.Parse<EngineKind>(engine), MediaRouter.Prefer(uri));
    }

    [Fact]
    public void Instagram_profile_url_reads_posts()
    {
        Assert.Equal(
            "https://www.instagram.com/susie_suey/posts/",
            MediaRouter.CanonicalPublicUrl("https://www.instagram.com/susie_suey/"));
        Assert.Equal(
            "https://www.instagram.com/albiol_xg/posts/",
            MediaRouter.CanonicalPublicUrl("https://www.instagram.com/stories/albiol_xg/"));
        Assert.Equal(
            "https://instagram.com/p/abc",
            MediaRouter.CanonicalPublicUrl("https://instagram.com/p/abc"));
        Assert.Equal(
            "https://ok.ru/video/20648036891",
            MediaRouter.CanonicalPublicUrl("https://www.ok.ru/videoembed/20648036891"));
        Assert.Equal(
            "https://ok.ru/video/20079905452",
            MediaRouter.CanonicalPublicUrl("https://m.ok.ru/video/20079905452"));
    }

    [Fact]
    public void Private_vs_instagram_session_errors()
    {
        Assert.True(MediaRouter.LooksPrivate("This account is private"));
        Assert.False(MediaRouter.LooksPrivate("Login required"));
        Assert.Equal("Only public profiles.", MediaRouter.PublicOnlyMessage);
        Assert.Equal(
            "Instagram hid the posts. Sign in when asked. Chrome can stay open.",
            MediaRouter.InstagramSessionMessage);
        Assert.True(MediaRouter.IsInstagram(new Uri("https://www.instagram.com/susie_suey/")));
        Assert.False(MediaRouter.IsInstagram(new Uri("https://www.youtube.com/watch?v=1")));
        Assert.True(MediaRouter.IsSocialPostHost(new Uri("https://www.instagram.com/p/abc")));
        Assert.True(MediaRouter.IsSocialPostHost(new Uri("https://www.threads.net/@x/post/1")));
        Assert.False(MediaRouter.IsSocialPostHost(new Uri("https://x.com/user/status/1")));
        Assert.True(MediaRouter.IsGalleryHost(new Uri("https://x.com/user/status/1")));
        Assert.True(MediaRouter.IsBunkr(new Uri("https://bunkr.cr/v/clip")));
        Assert.True(MediaRouter.IsBunkrFileOrAlbum(new Uri("https://bunkr.cr/f/file.mp4")));
        Assert.False(MediaRouter.IsBunkrFileOrAlbum(new Uri("https://bunkr.cr/")));
        Assert.Equal(
            "Paste a Bunkr file or album link, not the homepage.",
            MediaRouter.BunkrHomeMessage);
    }

    [Fact]
    public void SanitizeFolderName_strips_invalid_chars()
    {
        Assert.Equal("hello world", MediaRouter.SanitizeFolderName(@"hello/world:"));
        Assert.Equal("fetch", MediaRouter.SanitizeFolderName("   "));
        Assert.Equal("fetch", MediaRouter.SanitizeFolderName(".."));
        Assert.Equal("fetch", MediaRouter.SanitizeFolderName("."));
        Assert.Equal("fetch", MediaRouter.SanitizeFolderName("CON"));
        Assert.Equal("fetch", MediaRouter.SanitizeFolderName("CON.txt"));
        Assert.Equal(
            "Sydney Sweeney for Novig",
            MediaRouter.SanitizeFolderName("Sydney Sweeney for Novig. 📁"));
    }

    [Fact]
    public void CanWrite_temp_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "fetchit-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(FolderStore.CanWrite(root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
