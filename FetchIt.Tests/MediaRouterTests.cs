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
    [InlineData("https://x.com/user/status/1", true, "YtDlp")]
    [InlineData("https://twitter.com/user/status/1", true, "YtDlp")]
    [InlineData("https://instagram.com/p/abc", true, "GalleryDl")]
    [InlineData("https://www.instagram.com/stories/x/1", true, "GalleryDl")]
    [InlineData("https://www.threads.net/@x/post/1", true, "GalleryDl")]
    [InlineData("not a url", false, "YtDlp")]
    [InlineData("ftp://example.com/a", false, "YtDlp")]
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
        Assert.True(MediaRouter.LooksLikeMissingSession("'401 Unauthorized' for feed"));
        Assert.True(MediaRouter.LooksLikeMissingSession("HTTP redirect to login page"));
        Assert.Equal("Only public profiles.", MediaRouter.PublicOnlyMessage);
        Assert.Equal(
            "Instagram hid the posts. Sign in when asked. Chrome can stay open.",
            MediaRouter.InstagramSessionMessage);
        Assert.True(MediaRouter.IsInstagram(new Uri("https://www.instagram.com/susie_suey/")));
        Assert.False(MediaRouter.IsInstagram(new Uri("https://www.youtube.com/watch?v=1")));
    }

    [Fact]
    public void SanitizeFolderName_strips_invalid_chars()
    {
        Assert.Equal("hello world", MediaRouter.SanitizeFolderName(@"hello/world:"));
        Assert.Equal("fetch", MediaRouter.SanitizeFolderName("   "));
    }
}
