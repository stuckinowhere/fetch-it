using FetchIt.Services;

namespace FetchIt.Tests;

public class ThumbnailUrlTests
{
    [Theory]
    [InlineData("https://pbs.twimg.com/media/abc.jpg:large", "https://pbs.twimg.com/media/abc?format=jpg&name=small")]
    [InlineData("https://pbs.twimg.com/media/abc?format=jpg&name=orig", "https://pbs.twimg.com/media/abc?format=jpg&name=small")]
    [InlineData("https://pbs.twimg.com/media/abc.jpg", "https://pbs.twimg.com/media/abc?format=jpg&name=small")]
    public void X_preview_uses_small_jpg(string source, string expected)
        => Assert.Equal(expected, ThumbnailUrl.ForPreview(source));

    [Theory]
    [InlineData("https://pbs.twimg.com/media/abc.jpg:large", "https://pbs.twimg.com/media/abc?format=jpg&name=orig")]
    [InlineData("https://pbs.twimg.com/media/abc.png", "https://pbs.twimg.com/media/abc?format=png&name=orig")]
    [InlineData("https://video.twimg.com/ext_tw_video/1/pu/vid/720x720/a.mp4", "https://video.twimg.com/ext_tw_video/1/pu/vid/720x720/a.mp4")]
    public void X_save_uses_orig_still_or_keeps_video(string source, string expected)
        => Assert.Equal(expected, ThumbnailUrl.ForSave(source));

    [Fact]
    public void Pic_twitter_is_not_an_image()
    {
        Assert.False(ThumbnailUrl.LooksLikeImage("https://pic.twitter.com/abc"));
        Assert.True(ThumbnailUrl.LooksLikeImage("https://pbs.twimg.com/media/abc.jpg"));
        Assert.Equal(
            "https://pbs.twimg.com/media/abc.jpg",
            ThumbnailUrl.Pick("https://pic.twitter.com/abc", "https://pbs.twimg.com/media/abc.jpg"));
    }

    [Fact]
    public void Mid_width_scores_higher_than_huge()
    {
        Assert.True(ThumbnailUrl.ScoreThumbWidth(720) > ThumbnailUrl.ScoreThumbWidth(4096));
        Assert.True(ThumbnailUrl.ScoreThumbWidth(1280) > ThumbnailUrl.ScoreThumbWidth(120));
    }
}
