using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.Tests;

public class MediaRouterTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9wgXcQ", true, "YtDlp", false)]
    [InlineData("https://youtu.be/dQw4w9wgXcQ", true, "YtDlp", false)]
    [InlineData("https://www.tiktok.com/@x/video/1", true, "YtDlp", false)]
    [InlineData("https://vimeo.com/123", true, "YtDlp", false)]
    [InlineData("https://instagram.com/p/abc", true, "GalleryDl", true)]
    [InlineData("https://www.instagram.com/stories/x/1", true, "GalleryDl", true)]
    [InlineData("https://www.threads.net/@x/post/1", true, "GalleryDl", true)]
    [InlineData("https://x.com/user/status/1", true, "GalleryDl", true)]
    [InlineData("https://twitter.com/user/status/1", true, "GalleryDl", true)]
    [InlineData("not a url", false, "YtDlp", false)]
    [InlineData("ftp://example.com/a", false, "YtDlp", false)]
    public void RoutesHosts(string text, bool valid, string engine, bool login)
    {
        var parsed = MediaRouter.TryParseHttpUrl(text, out var uri);
        Assert.Equal(valid, parsed);
        if (!valid)
            return;

        Assert.Equal(Enum.Parse<EngineKind>(engine), MediaRouter.Prefer(uri));
        Assert.Equal(login, MediaRouter.NeedsLoginRow(uri));
    }

    [Fact]
    public void SanitizeFolderName_strips_invalid_chars()
    {
        Assert.Equal("hello world", MediaRouter.SanitizeFolderName(@"hello/world:"));
        Assert.Equal("fetch", MediaRouter.SanitizeFolderName("   "));
    }
}
