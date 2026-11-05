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

    [Fact]
    public void YtDlp_prefers_mid_thumbnail_over_huge()
    {
        const string json =
            """{"title":"t","ext":"mp4","extractor_key":"Youtube","thumbnails":[{"url":"https://a/huge.jpg","width":4096},{"url":"https://a/mid.jpg","width":480}]}""";
        var probe = YtDlpParser.Parse(json);
        Assert.Equal("https://a/mid.jpg", Assert.Single(probe.Items).ThumbnailUrl);
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
    public void GalleryDl_skips_pic_twitter_display_url()
    {
        const string json =
            """[3, "https://pbs.twimg.com/media/abc.jpg", {"category":"twitter","extension":"jpg","type":"photo","display_url":"https://pic.twitter.com/xyz","thumbnail":"https://pic.twitter.com/xyz"}]""";
        var probe = GalleryDlParser.Parse(json);
        Assert.Equal("https://pbs.twimg.com/media/abc.jpg", Assert.Single(probe.Items).ThumbnailUrl);
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
    }

    [Fact]
    public void Instagram_files_keep_their_id_and_overlapping_tabs_count_once()
    {
        const string json = """
            [
              [3, "https://cdn.example/a.jpg", {"category":"instagram","filename":"643588901_n","extension":"webp"}],
              [3, "https://cdn.example/a2.jpg", {"category":"instagram","filename":"643588901_n","extension":"webp"}],
              [3, "https://cdn.example/b.mp4", {"category":"instagram","filename":"777_n","extension":"mp4"}],
              [-1, {"error":"NotFoundError","message":"no stories"}]
            ]
            """;
        var probe = GalleryDlParser.Parse(json);

        Assert.Equal(["643588901_n.webp", "777_n.mp4"], probe.Items.Select(item => item.SaveName));
        Assert.Equal(1, probe.ImageCount);
        Assert.Equal(1, probe.VideoCount);
    }

    [Theory]
    [InlineData("../../evil", "jpg")]
    [InlineData("a/b", "jpg")]
    [InlineData("a:b", "jpg")]
    [InlineData("name", "e/xe")]
    [InlineData("name", "toolong")]
    public void Instagram_names_with_path_tricks_are_refused(string filename, string extension)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { category = "instagram", filename, extension }));
        Assert.Null(GalleryDlParser.InstagramSaveName(doc.RootElement, extension));
    }

    [Fact]
    public void Other_sites_keep_the_default_names()
    {
        var probe = GalleryDlParser.Parse("""[[3, "https://x.example/a.jpg", {"category":"twitter","filename":"a","extension":"jpg"}]]""");
        Assert.Null(Assert.Single(probe.Items).SaveName);
    }

    [Theory]
    [InlineData("[download]  12.3% of 10.00MiB at 1.00MiB/s ETA 00:08", 12.3)]
    [InlineData("100%", 100)]
    public void YtDlp_parses_percent(string line, double expected)
        => Assert.Equal(expected, YtDlpParser.TryParsePercent(line));

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
        var downloads = FolderStore.WindowsDownloads();
        Assert.Equal("Downloads", ViewModels.MainViewModel.FolderDisplay(downloads));
    }


    [Fact]
    public void YtDlp_parses_flat_playlist_entries()
    {
        // --flat-playlist entries carry no formats/ext/vcodec: still videos.
        const string json = """
            {
              "_type": "playlist",
              "title": "Flat",
              "extractor_key": "YoutubeTab",
              "entries": [
                { "_type": "url", "ie_key": "Youtube", "id": "a1", "url": "https://www.youtube.com/watch?v=a1", "title": "First", "duration": 61,
                  "thumbnails": [ { "url": "https://i.ytimg.com/vi/a1/hqdefault.jpg", "height": 360, "width": 480 } ] },
                { "_type": "url", "ie_key": "Youtube", "id": "b2", "url": "https://www.youtube.com/watch?v=b2", "title": "Second", "duration": null }
              ]
            }
            """;
        var probe = YtDlpParser.Parse(json);
        Assert.Equal("Flat", probe.Title);
        Assert.Equal(2, probe.VideoCount);
        Assert.Equal(0, probe.ImageCount);
        Assert.Equal("First", probe.Items[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(61), probe.Items[0].Duration);
        Assert.Equal("https://i.ytimg.com/vi/a1/hqdefault.jpg", probe.Items[0].ThumbnailUrl);
        Assert.Equal("Second", probe.Items[1].Title);
        Assert.Null(probe.Items[1].Duration);
    }
}
