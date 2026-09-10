using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.Tests;

public class E2eProbeTests
{
    [Fact]
    public async Task Probe_public_youtube_returns_video()
    {
        if (ToolBootstrapper.TryFind() is null)
            return;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var statuses = new List<string>();
        var progress = new Progress<FetchProgress>(update =>
        {
            if (!string.IsNullOrWhiteSpace(update.Status))
                statuses.Add(update.Status);
        });

        var probe = await new MediaFetcher().ProbeAsync(
            "https://www.youtube.com/watch?v=jNQXAC9IVRw",
            progress,
            cts.Token);

        Assert.True(probe.VideoCount >= 1, $"expected a video, got {probe.Summary}");
        Assert.NotEmpty(probe.Items);
        Assert.Contains(statuses, status => status.Contains("Reading", StringComparison.OrdinalIgnoreCase)
                                            || status.Contains("tools", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://www.tiktok.com/@scout2015/video/6718335390845095173")]
    [InlineData("https://www.dailymotion.com/video/x7tgad0")]
    [InlineData("https://www.reddit.com/r/videos/comments/6rrwyj/that_small_heart_attack/")]
    public async Task Probe_public_video_sites(string url)
    {
        if (ToolBootstrapper.TryFind() is null)
            return;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var probe = await new MediaFetcher().ProbeAsync(url, null, cts.Token);
        Assert.True(probe.FileCount >= 1, probe.Summary);
        Assert.Equal(EngineKind.YtDlp, probe.Engine);
    }

    [Fact]
    public async Task Probe_okru_public_video_when_reachable()
    {
        if (ToolBootstrapper.TryFind() is null)
            return;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var probe = await new MediaFetcher().ProbeAsync(
                "https://ok.ru/video/1484130554189",
                null,
                cts.Token);
            Assert.True(probe.FileCount >= 1, probe.Summary);
        }
        catch (InvalidOperationException)
        {
            // ok.ru often times out or geo-blocks from some networks.
        }
    }

    [Fact]
    public async Task Probe_instagram_stories_attempts_public_profile()
    {
        if (ToolBootstrapper.TryFind() is null)
            return;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var statuses = new List<string>();
        var progress = new Progress<FetchProgress>(update =>
        {
            if (!string.IsNullOrWhiteSpace(update.Status))
                statuses.Add(update.Status);
        });

        try
        {
            var probe = await new MediaFetcher().ProbeAsync(
                "https://www.instagram.com/stories/albiol_xg/",
                progress,
                cts.Token);
            Assert.True(probe.FileCount >= 1, probe.Summary);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Equal(MediaRouter.InstagramSessionMessage, ex.Message);
        }

        Assert.Contains(statuses, status => status.Contains("public profile", StringComparison.OrdinalIgnoreCase)
                                            || status.Contains("Reading", StringComparison.OrdinalIgnoreCase)
                                            || status.Contains("tools", StringComparison.OrdinalIgnoreCase));
    }
}
