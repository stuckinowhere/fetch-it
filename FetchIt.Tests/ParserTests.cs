using FetchIt.Services;

namespace FetchIt.Tests;

public class ParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void YtDlp_parses_youtube_video()
    {
        var probe = YtDlpParser.Parse(Fixture("youtube-video.json"));
        Assert.Equal("Never Gonna Give You Up", probe.Title);
        Assert.Equal("YouTube", probe.Site);
        Assert.Equal(1, probe.VideoCount);
        Assert.Equal(0, probe.ImageCount);
        Assert.Equal(TimeSpan.FromSeconds(212), probe.Duration);
        Assert.Contains("1 video", probe.Summary);
    }

    [Fact]
    public void YtDlp_parses_playlist()
    {
        var probe = YtDlpParser.Parse(Fixture("youtube-playlist.json"));
        Assert.Equal("Best of", probe.Title);
        Assert.Equal(2, probe.VideoCount);
        Assert.Equal(TimeSpan.FromSeconds(150), probe.Duration);
    }

    [Fact]
    public void GalleryDl_parses_instagram_mix()
    {
        var probe = GalleryDlParser.Parse(Fixture("instagram-post.json"));
        Assert.Equal("Instagram", probe.Site);
        Assert.Equal(2, probe.ImageCount);
        Assert.Equal(1, probe.VideoCount);
        Assert.Contains("1 video", probe.Summary);
        Assert.Contains("2 images", probe.Summary);
    }

    [Fact]
    public void GalleryDl_parses_x_photo()
    {
        var probe = GalleryDlParser.Parse(Fixture("twitter-photo.json"));
        Assert.Equal("X", probe.Site);
        Assert.Equal(1, probe.ImageCount);
        Assert.Equal(0, probe.VideoCount);
    }

    [Theory]
    [InlineData("[download]  12.3% of 10.00MiB at 1.00MiB/s ETA 00:08", 12.3)]
    [InlineData("100%", 100)]
    public void YtDlp_parses_percent(string line, double expected)
        => Assert.Equal(expected, YtDlpParser.TryParsePercent(line));

    [Fact]
    public void YtDlp_parses_size_status()
        => Assert.Equal("2.1 MiB / 12.4 MiB", YtDlpParser.TryParseSizeStatus("  2.1 MiB / 12.4 MiB at 1MiB/s"));

    [Fact]
    public void FolderDisplay_uses_Downloads()
    {
        var downloads = ViewModels.MainViewModel.DefaultDownloads();
        Assert.Equal("Downloads", ViewModels.MainViewModel.FolderDisplay(downloads));
    }
}
