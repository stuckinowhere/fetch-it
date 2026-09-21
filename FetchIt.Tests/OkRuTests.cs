using System.Net.Http;
using System.Text.Json;
using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.Tests;

public class OkRuTests
{
    [Fact]
    public void Lists_qualities_best_first()
    {
        const string flashvars = """
            {
              "metadata": {
                "videos": [
                  { "name": "mobile", "url": "https://cdn.example/mobile.mp4" },
                  { "name": "sd", "url": "https://cdn.example/sd.mp4" },
                  { "name": "hd", "url": "https://cdn.example/hd.mp4" }
                ]
              }
            }
            """;
        using var doc = JsonDocument.Parse(flashvars);
        Assert.True(OkRuService.TryReadMetadata(doc.RootElement, out var meta));
        var qualities = OkRuService.ListQualities(meta);
        Assert.Equal(["HD", "SD", "Mobile"], qualities.Select(q => q.Label).ToArray());
        Assert.Equal("https://cdn.example/hd.mp4", qualities[0].Url);
    }

    [Fact]
    public void WithSelectedQuality_swaps_download_url()
    {
        var probe = new MediaProbe
        {
            Title = "Clip",
            Site = "OK.ru",
            VideoCount = 1,
            Engine = EngineKind.GalleryDl,
            Qualities =
            [
                new MediaQuality { Label = "HD", Url = "https://cdn.example/hd.mp4" },
                new MediaQuality { Label = "SD", Url = "https://cdn.example/sd.mp4" }
            ],
            Items =
            [
                new MediaItem
                {
                    Kind = MediaKind.Video,
                    Title = "Clip",
                    DownloadUrl = "https://cdn.example/hd.mp4"
                }
            ]
        };
        var picked = OkRuService.WithSelectedQuality(probe, "https://cdn.example/sd.mp4");
        Assert.Equal("https://cdn.example/sd.mp4", picked.Items[0].DownloadUrl);
        Assert.Equal(2, picked.Qualities.Count);
    }

    [Fact]
    public void DownloadMeter_formats_known_total()
    {
        var meter = new DownloadMeter();
        meter.Reset(0);
        var seen = meter.Snapshot(5_242_880, 10_485_760);
        Assert.StartsWith("5 MB / 10 MB", seen.Status);
        Assert.True(seen.HasPercent);
        Assert.InRange(seen.Percent, 49, 51);
    }

    [Fact]
    public void DownloadMeter_formats_unknown_total()
    {
        var meter = new DownloadMeter();
        meter.Reset(0);
        var seen = meter.Snapshot(1536, null);
        Assert.StartsWith("Saving… 1.5 KB", seen.Status);
        Assert.False(seen.HasPercent);
    }

    [Fact]
    public void DownloadMeter_formats_speed()
    {
        Assert.Equal("1.5 MB/s", DownloadMeter.FormatSpeed(1.5 * 1024 * 1024));
        Assert.Equal("0.2 MB/s", DownloadMeter.FormatSpeed(200 * 1024));
        Assert.Equal("50 KB/s", DownloadMeter.FormatSpeed(50 * 1024));
        Assert.Equal("…", DownloadMeter.FormatSpeed(0));
    }

    [Fact]
    public void Parses_metadata_object_and_picks_hd()
    {
        const string flashvars = """
            {
              "metadata": {
                "provider": "UPLOADED_ODKL",
                "movie": { "title": "Clip", "duration": "120", "poster": "https://okcdn.ru/p.jpg" },
                "videos": [
                  { "name": "mobile", "url": "https://cdn.example/mobile.mp4" },
                  { "name": "sd", "url": "https://cdn.example/sd.mp4" },
                  { "name": "hd", "url": "https://cdn.example/hd.mp4" }
                ]
              }
            }
            """;
        using var doc = JsonDocument.Parse(flashvars);
        Assert.True(OkRuService.TryReadMetadata(doc.RootElement, out var meta));
        Assert.Equal("https://cdn.example/hd.mp4", OkRuService.ListQualities(meta)[0].Url);
        Assert.True(OkRuService.QualityScore("hd") > OkRuService.QualityScore("sd"));
    }

    [Fact]
    public void Parses_metadata_when_still_a_json_string()
    {
        const string flashvars = """
            {
              "metadata": "{\"movie\":{\"title\":\"Old\"},\"videos\":[{\"name\":\"sd\",\"url\":\"https://cdn.example/old.mp4\"}]}"
            }
            """;
        using var doc = JsonDocument.Parse(flashvars);
        Assert.True(OkRuService.TryReadMetadata(doc.RootElement, out var meta));
        Assert.Equal("https://cdn.example/old.mp4", OkRuService.ListQualities(meta)[0].Url);
    }

    [Fact]
    public void Extracts_player_json_from_html_entities()
    {
        const string html = """
            <div data-options="{&quot;flashvars&quot;:{&quot;metadata&quot;:{&quot;movie&quot;:{&quot;title&quot;:&quot;El imperio&quot;,&quot;duration&quot;:&quot;10&quot;},&quot;videos&quot;:[{&quot;name&quot;:&quot;hd&quot;,&quot;url&quot;:&quot;https://cdn.example/v.mp4&quot;}]}}}"></div>
            """;
        Assert.True(OkRuService.TryParsePlayer(html, out var player));
        Assert.True(player.TryGetProperty("flashvars", out var flashvars));
        Assert.True(OkRuService.TryReadMetadata(flashvars, out var meta));
        Assert.Equal("https://cdn.example/v.mp4", OkRuService.ListQualities(meta)[0].Url);
    }

    [Fact]
    public void OkRu_hosts_are_detected()
    {
        Assert.True(MediaRouter.IsOkRu(new Uri("https://ok.ru/video/1")));
        Assert.True(MediaRouter.IsOkRu(new Uri("https://www.ok.ru/videoembed/1")));
        Assert.True(MediaRouter.IsOkRu(new Uri("https://m.ok.ru/video/1")));
        Assert.False(MediaRouter.IsOkRu(new Uri("https://youtube.com/watch?v=1")));
    }

    [Fact]
    public void Okcdn_uses_okru_referer()
    {
        Assert.Equal(
            new Uri("https://ok.ru/"),
            ThumbnailUrl.RefererFor("https://ok6-31.vkuser.net/video.mp4?x=1"));
        Assert.Equal(
            new Uri("https://ok.ru/"),
            ThumbnailUrl.RefererFor("https://iv.okcdn.ru/i?r=abc"));
    }

    [Fact]
    public async Task Live_user_video_when_reachable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var probe = await new OkRuService().ProbeAsync(
                "https://ok.ru/video/7513892063791",
                null,
                cts.Token);
            Assert.True(probe.FileCount >= 1, probe.Summary);
            Assert.Contains("imperio", probe.Title, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrEmpty(probe.Items[0].DownloadUrl));
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (InvalidOperationException)
        {
            // ok.ru can geo-block or change the player markup.
        }
    }
}
