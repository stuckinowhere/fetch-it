using FetchIt.Models;
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
        var item = Assert.Single(probe.Items);
        Assert.Equal(MediaKind.Video, item.Kind);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9wgXcQ/hqdefault.jpg", item.ThumbnailUrl);
        Assert.Equal("3:32", item.Overlay);
    }

    [Fact]
    public void YtDlp_parses_playlist()
    {
        var probe = YtDlpParser.Parse(Fixture("youtube-playlist.json"));
        Assert.Equal("Best of", probe.Title);
        Assert.Equal(2, probe.VideoCount);
        Assert.Equal(TimeSpan.FromSeconds(150), probe.Duration);
        Assert.Equal(2, probe.Items.Count);
        Assert.Equal("One", probe.Items[0].Title);
        Assert.Equal(MediaKind.Video, probe.Items[0].Kind);
        Assert.Equal("https://example.com/one.jpg", probe.Items[0].ThumbnailUrl);
        Assert.Equal("https://example.com/two.jpg", probe.Items[1].ThumbnailUrl);
    }

    [Fact]
    public void YtDlp_uses_largest_thumbnail()
    {
        const string json =
            """{"title":"t","ext":"mp4","extractor_key":"Youtube","thumbnails":[{"url":"https://a/small.jpg","width":120},{"url":"https://a/big.jpg","width":1280}]}""";
        var probe = YtDlpParser.Parse(json);
        Assert.Equal("https://a/big.jpg", Assert.Single(probe.Items).ThumbnailUrl);
    }

    [Theory]
    [InlineData("Odnoklassniki", "OK.ru")]
    [InlineData("TikTok", "TikTok")]
    [InlineData("Dailymotion", "Dailymotion")]
    [InlineData("Reddit", "Reddit")]
    public void YtDlp_labels_common_sites(string extractor, string site)
    {
        var probe = YtDlpParser.Parse(
            $$"""{"title":"t","ext":"mp4","extractor_key":"{{extractor}}","duration":5}""");
        Assert.Equal(site, probe.Site);
        Assert.Equal(1, probe.VideoCount);
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
        Assert.Equal(3, probe.Items.Count);
        Assert.Equal(MediaKind.Image, probe.Items[0].Kind);
        Assert.Equal("https://example.com/1.jpg", probe.Items[0].ThumbnailUrl);
        Assert.Equal(MediaKind.Video, probe.Items[2].Kind);
        Assert.Equal("https://example.com/3.jpg", probe.Items[2].ThumbnailUrl);
    }

    [Fact]
    public void GalleryDl_parses_x_photo()
    {
        var probe = GalleryDlParser.Parse(Fixture("twitter-photo.json"));
        Assert.Equal("X", probe.Site);
        Assert.Equal(1, probe.ImageCount);
        Assert.Equal(0, probe.VideoCount);
        var item = Assert.Single(probe.Items);
        Assert.Equal(MediaKind.Image, item.Kind);
        Assert.Equal("https://example.com/p.jpg", item.ThumbnailUrl);
        Assert.Equal("IMAGE", item.Overlay);
    }

    [Fact]
    public void GalleryDl_parses_x_photo_gallery()
    {
        var probe = GalleryDlParser.Parse(Fixture("twitter-gallery.json"));
        Assert.Equal("X", probe.Site);
        Assert.Equal("Sydney Sweeney for Novig", probe.Title);
        Assert.Equal(4, probe.ImageCount);
        Assert.Equal(0, probe.VideoCount);
        Assert.Equal(EngineKind.GalleryDl, probe.Engine);
        Assert.Contains("4 images", probe.Summary);
        Assert.Equal(4, probe.Items.Count);
        Assert.All(probe.Items, item => Assert.Equal(MediaKind.Image, item.Kind));
        Assert.Equal("https://example.com/a.jpg", probe.Items[0].ThumbnailUrl);
        Assert.Equal("https://example.com/a.jpg", probe.Items[0].DownloadUrl);
        Assert.Equal("https://example.com/d.jpg", probe.Items[3].ThumbnailUrl);
    }

    [Fact]
    public void GalleryDl_keeps_x_photos_when_counts_contain_401()
    {
        var json = """
            [
              [2, {"category": "twitter", "content": "post", "favorite_count": 401}],
              [3, "https://example.com/a.jpg", {"category": "twitter", "extension": "jpg", "type": "photo", "favorite_count": 401}],
              [3, "https://example.com/b.jpg", {"category": "twitter", "extension": "jpg", "type": "photo", "favorite_count": 401}],
              [3, "https://example.com/c.jpg", {"category": "twitter", "extension": "jpg", "type": "photo", "favorite_count": 401}]
            ]
            """;
        Assert.False(MediaRouter.LooksLikeMissingSession(json));
        var probe = GalleryDlParser.Parse(json);
        Assert.Equal(3, probe.ImageCount);
        Assert.Equal(0, probe.VideoCount);
    }

    [Fact]
    public void GalleryDl_parses_x_mixed_photo_and_video()
    {
        var probe = GalleryDlParser.Parse(Fixture("twitter-mixed.json"));
        Assert.Equal("X", probe.Site);
        Assert.Equal(2, probe.ImageCount);
        Assert.Equal(1, probe.VideoCount);
        Assert.Contains("1 video", probe.Summary);
        Assert.Contains("2 images", probe.Summary);
        Assert.Equal(3, probe.Items.Count);
        Assert.Equal(MediaKind.Image, probe.Items[0].Kind);
        Assert.Equal(MediaKind.Video, probe.Items[2].Kind);
        Assert.Equal("https://example.com/c.jpg", probe.Items[2].ThumbnailUrl);
    }

    [Fact]
    public void GalleryDl_ignores_job_and_login_dumps()
    {
        var job = GalleryDlParser.Parse("""[[6,"https://www.instagram.com/x/posts/",{"category":"instagram"}]]""");
        Assert.Empty(job.Items);
        Assert.Equal(0, job.FileCount);

        var login = GalleryDlParser.Parse("""[[-1,{"error":"AbortExtraction","message":"HTTP redirect to login page"}]]""");
        Assert.Empty(login.Items);
        Assert.True(MediaRouter.LooksLikeMissingSession("HTTP redirect to login page"));
    }

    [Fact]
    public void PreviewCap_reports_extra()
    {
        var items = Enumerable.Range(0, 52)
            .Select(i => new MediaItem { Kind = MediaKind.Image, Title = $"{i}" })
            .ToList();
        var probe = new MediaProbe { Items = items, ImageCount = 52 };
        Assert.Equal(2, probe.ExtraCount);
        Assert.Equal(50, probe.PreviewItems.Count());
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
    public void YtDlp_parses_download_speed_and_remaining()
    {
        var parsed = YtDlpParser.TryParseDownloadProgress(
            "[download]  12.3% of 10.00MiB at 1.00MiB/s ETA 00:08");
        Assert.NotNull(parsed);
        Assert.Equal(12.3, parsed.Value.Percent);
        Assert.Equal("1.0 MB/s  ·  8.8 MB left", parsed.Value.Status);
    }

    [Fact]
    public void YtDlp_parses_fragment_download_progress()
    {
        var parsed = YtDlpParser.TryParseDownloadProgress(
            "[download]   5.0% of ~  234.56MiB at  2.10MiB/s ETA 01:45 (frag 3/20)");
        Assert.NotNull(parsed);
        Assert.Equal(5.0, parsed.Value.Percent);
        Assert.Equal("2.1 MB/s  ·  223 MB left", parsed.Value.Status);
    }

    [Fact]
    public void YtDlp_hides_remaining_when_finished()
    {
        var parsed = YtDlpParser.TryParseDownloadProgress(
            "[download] 100% of 19.62MiB in 00:08 at 2.31MiB/s");
        Assert.NotNull(parsed);
        Assert.Equal(100, parsed.Value.Percent);
        Assert.Equal("2.3 MB/s", parsed.Value.Status);
    }

    [Fact]
    public void YtDlp_parses_merge_as_finishing()
    {
        var parsed = YtDlpParser.TryParseDownloadProgress("[Merger] Merging formats into \"clip.mp4\"");
        Assert.NotNull(parsed);
        Assert.Equal(100, parsed.Value.Percent);
        Assert.Equal("Merging…", parsed.Value.Status);
    }

    [Fact]
    public void FolderDisplay_uses_Downloads()
    {
        var downloads = ViewModels.MainViewModel.DefaultDownloads();
        Assert.Equal("Downloads", ViewModels.MainViewModel.FolderDisplay(downloads));
        Assert.Equal(
            FolderStore.WindowsDownloads(),
            ViewModels.MainViewModel.DefaultDownloads());
    }

    [Theory]
    [InlineData("https://pbs.twimg.com/media/abc?format=jpg&name=orig", true)]
    [InlineData("https://i.ytimg.com/vi/dQw4w9wgXcQ/hqdefault.jpg", false)]
    public void Preview_uses_x_referer_only_for_twitter_cdn(string url, bool expected)
        => Assert.Equal(expected, ViewModels.PreviewCard.NeedsXReferer(url));
}
