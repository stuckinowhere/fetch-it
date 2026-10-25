using System.Text.Json;
using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.Tests;

public class KemonoTests
{
    [Theory]
    [InlineData("https://kemono.cr/patreon/user/17376395", true, "https://kemono.cr", "patreon", "17376395", null)]
    [InlineData("https://coomer.su/onlyfans/user/alice/post/999", true, "https://coomer.su", "onlyfans", "alice", "999")]
    [InlineData("https://kemono.party/fanbox/user/42?q=tag", true, "https://kemono.party", "fanbox", "42", null)]
    [InlineData("https://kemono.cr/discord/server/123", false, null, null, null, null)]
    [InlineData("https://youtube.com/watch?v=123", false, null, null, null, null)]
    [InlineData("not a url", false, null, null, null, null)]
    public void TryParse_targets(string url, bool expected, string? root, string? service, string? user, string? post)
    {
        var ok = KemonoService.TryParse(url, out var target);
        Assert.Equal(expected, ok);
        if (!ok)
            return;

        Assert.Equal(root, target.Root);
        Assert.Equal(service, target.Service);
        Assert.Equal(user, target.User);
        Assert.Equal(post, target.PostId);
    }

    [Fact]
    public void SiteRoot_recognizes_kemono_and_coomer_domains()
    {
        Assert.Equal("https://kemono.cr", KemonoService.SiteRoot("kemono.cr"));
        Assert.Equal("https://kemono.cr", KemonoService.SiteRoot("img.kemono.cr"));
        Assert.Equal("https://coomer.su", KemonoService.SiteRoot("n2.coomer.su"));
        Assert.Null(KemonoService.SiteRoot("google.com"));
        Assert.Null(KemonoService.SiteRoot("kemono.org"));
    }

    [Fact]
    public void BuildProbe_extracts_files_and_attachments_into_subfolders()
    {
        var target = new KemonoService.Target("https://kemono.cr", "patreon", "17376395", null, null);
        var json = """
        [
            {
                "id": "1001",
                "title": "Beach Shoot Part 1",
                "file": { "name": "cover.jpg", "path": "/ab/cd/cover.jpg" },
                "attachments": [
                    { "name": "beach_01.png", "path": "/ab/cd/att1.png" },
                    { "name": "beach_02.png", "path": "/ab/cd/att2.png" }
                ]
            },
            {
                "id": "1002",
                "title": "Studio Video",
                "file": { "name": "clip.mp4", "path": "/ef/gh/clip.mp4" },
                "attachments": []
            }
        ]
        """;

        using var doc = JsonDocument.Parse(json);
        var posts = doc.RootElement.EnumerateArray().ToList();
        var probe = KemonoService.BuildProbe(target, "studiolight", posts);

        Assert.Equal("studiolight", probe.Title);
        Assert.Equal("Kemono", probe.Site);
        Assert.Equal(4, probe.Items.Count);
        Assert.Equal(3, probe.ImageCount);
        Assert.Equal(1, probe.VideoCount);

        // Check subfolder structure: creator / post [id] / NN_name.ext
        var first = probe.Items[0];
        Assert.Equal(MediaKind.Image, first.Kind);
        Assert.Equal("https://kemono.cr/data/ab/cd/cover.jpg", first.DownloadUrl);
        Assert.Equal("https://img.kemono.cr/thumbnail/data/ab/cd/cover.jpg", first.FallbackUrl);
        Assert.NotNull(first.SaveName);
        Assert.Contains("studiolight", first.SaveName);
        Assert.Contains("Beach Shoot Part 1 [1001]", first.SaveName);
        Assert.EndsWith("01_cover.jpg", first.SaveName);

        var video = probe.Items[3];
        Assert.Equal(MediaKind.Video, video.Kind);
        Assert.Null(video.FallbackUrl); // Videos do not fallback to img thumbnails
        Assert.NotNull(video.SaveName);
        Assert.Contains("Studio Video [1002]", video.SaveName);
        Assert.EndsWith("01_clip.mp4", video.SaveName);
    }

    [Fact]
    public void PlanJobs_resolves_correct_destinations_with_duplicates()
    {
        var temp = Path.Combine(Path.GetTempPath(), "KemonoTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var probe = new MediaProbe
            {
                Title = "TestCreator",
                Engine = EngineKind.GalleryDl,
                Items = new[]
                {
                    new MediaItem
                    {
                        DownloadUrl = "https://kemono.cr/data/1.jpg",
                        FallbackUrl = "https://img.kemono.cr/thumbnail/data/1.jpg",
                        SaveName = Path.Combine("Creator", "Post1", "01_photo.jpg")
                    },
                    new MediaItem
                    {
                        DownloadUrl = "https://kemono.cr/data/2.jpg",
                        FallbackUrl = "https://img.kemono.cr/thumbnail/data/2.jpg",
                        SaveName = Path.Combine("Creator", "Post1", "02_photo.jpg")
                    }
                }
            };

            var jobs = KemonoService.PlanJobs(temp, probe, DuplicateChoice.KeepBoth);
            Assert.Equal(2, jobs.Count);
            Assert.Equal("https://kemono.cr/data/1.jpg", jobs[0].Url);
            Assert.Equal("https://img.kemono.cr/thumbnail/data/1.jpg", jobs[0].Fallback);
            Assert.Contains(Path.Combine("Creator", "Post1"), jobs[0].Path);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public void PreviewNote_formats_summary()
    {
        var note = KemonoService.PreviewNote(10, 10);
        Assert.Contains("Saved 10 files as previews", note);
        Assert.Contains("blocked on this network", note);

        var mixed = KemonoService.PreviewNote(20, 5);
        Assert.Contains("Saved 20 files (5 as previews)", mixed);
    }
}
