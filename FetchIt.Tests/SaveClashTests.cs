using FetchIt.Models;
using FetchIt.Services;
using FetchIt.Views;

namespace FetchIt.Tests;

public class SaveClashTests
{
    [Fact]
    public void Planned_gallery_uses_title_index_and_url_extension()
    {
        var probe = new MediaProbe
        {
            Title = "Sydney Sweeney for Novig.",
            Engine = EngineKind.GalleryDl,
            Items =
            [
                new MediaItem
                {
                    Kind = MediaKind.Image,
                    DownloadUrl = "https://pbs.twimg.com/media/abc.jpg:large"
                },
                new MediaItem
                {
                    Kind = MediaKind.Image,
                    DownloadUrl = "https://pbs.twimg.com/media/def.png"
                }
            ]
        };

        Assert.Equal(
            ["Sydney Sweeney for Novig_1.jpg", "Sydney Sweeney for Novig_2.png"],
            SaveClash.PlannedNames(probe));
    }

    [Fact]
    public void Existing_names_only_lists_files_already_on_disk()
    {
        var dest = Path.Combine(Path.GetTempPath(), "fetchit-clash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            File.WriteAllText(Path.Combine(dest, "clip.mp4"), "x");
            var probe = new MediaProbe
            {
                Title = "clip",
                Engine = EngineKind.YtDlp
            };
            Assert.Equal(["clip.mp4"], SaveClash.ExistingNames(dest, probe));

            var missing = new MediaProbe { Title = "other", Engine = EngineKind.YtDlp };
            Assert.Empty(SaveClash.ExistingNames(dest, missing));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void UniquePath_adds_a_suffix_when_the_file_exists()
    {
        var dest = Path.Combine(Path.GetTempPath(), "fetchit-unique-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            File.WriteAllText(Path.Combine(dest, "a.jpg"), "x");
            var next = SaveClash.UniquePath(dest, "a.jpg");
            Assert.Equal(Path.Combine(dest, "a_2.jpg"), next);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void PlanDirectFiles_assigns_names_before_parallel_download()
    {
        var dest = Path.Combine(Path.GetTempPath(), "fetchit-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            var probe = new MediaProbe
            {
                Title = "batch",
                Engine = EngineKind.GalleryDl,
                Items =
                [
                    new MediaItem { Kind = MediaKind.Image, DownloadUrl = "https://example.com/a.jpg" },
                    new MediaItem { Kind = MediaKind.Image, DownloadUrl = "https://example.com/b.jpg" },
                    new MediaItem { Kind = MediaKind.Image, DownloadUrl = "https://example.com/c.jpg" }
                ]
            };
            var jobs = GalleryDlService.PlanDirectFiles(dest, probe, DuplicateChoice.Overwrite);
            Assert.Equal(3, jobs.Count);
            Assert.Equal(3, jobs.Select(j => j.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.True(GalleryDlService.DirectParallel >= 2);
            Assert.Equal("https://example.com/a.jpg", jobs[0].Url);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void Direct_download_is_skipped_when_probe_hit_the_file_cap()
    {
        var dest = Path.Combine(Path.GetTempPath(), "fetchit-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            var full = GalleryItems(GalleryDlService.ProbeFileLimit);
            var jobs = GalleryDlService.PlanDirectFiles(dest, full, DuplicateChoice.Overwrite);
            Assert.Equal(GalleryDlService.ProbeFileLimit, jobs.Count);
            Assert.False(GalleryDlService.CanDownloadDirect(full, jobs));

            var under = GalleryItems(GalleryDlService.ProbeFileLimit - 1);
            var underJobs = GalleryDlService.PlanDirectFiles(dest, under, DuplicateChoice.Overwrite);
            Assert.True(GalleryDlService.CanDownloadDirect(under, underJobs));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void PlanDirectFiles_keeps_item_index_when_a_url_is_missing()
    {
        var dest = Path.Combine(Path.GetTempPath(), "fetchit-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            var probe = new MediaProbe
            {
                Title = "mix",
                Engine = EngineKind.GalleryDl,
                Items =
                [
                    new MediaItem { Kind = MediaKind.Image, DownloadUrl = "https://example.com/a.jpg" },
                    new MediaItem { Kind = MediaKind.Video },
                    new MediaItem { Kind = MediaKind.Image, DownloadUrl = "https://example.com/c.jpg" }
                ]
            };
            var jobs = GalleryDlService.PlanDirectFiles(dest, probe, DuplicateChoice.Overwrite);
            Assert.Equal(2, jobs.Count);
            Assert.EndsWith("mix_1.jpg", jobs[0].Path, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("mix_3.jpg", jobs[1].Path, StringComparison.OrdinalIgnoreCase);
            Assert.False(GalleryDlService.CanDownloadDirect(probe, jobs));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void Planned_yt_playlist_numbers_each_file()
    {
        var probe = new MediaProbe
        {
            Title = "Best of",
            Engine = EngineKind.YtDlp,
            Items =
            [
                new MediaItem { Kind = MediaKind.Video, Title = "One" },
                new MediaItem { Kind = MediaKind.Video, Title = "Two" },
                new MediaItem { Kind = MediaKind.Video, Title = "Three" }
            ]
        };
        Assert.Equal(["Best of_001.mp4", "Best of_002.mp4", "Best of_003.mp4"], SaveClash.PlannedNames(probe));
    }

    [Fact]
    public void Yt_output_template_numbers_a_batch()
    {
        var dest = Path.Combine("D:", "Descargas");
        var one = YtDlpService.OutputTemplate(dest, "clip", 1, DuplicateChoice.Overwrite);
        Assert.Equal(Path.Combine(dest, "clip.%(ext)s"), one);

        var many = YtDlpService.OutputTemplate(dest, "Best of", 3, DuplicateChoice.Overwrite);
        Assert.Equal(Path.Combine(dest, "Best of_%(autonumber)03d.%(ext)s"), many);
    }

    private static MediaProbe GalleryItems(int count)
        => new()
        {
            Title = "album",
            Engine = EngineKind.GalleryDl,
            ImageCount = count,
            Items = Enumerable.Range(1, count)
                .Select(i => new MediaItem
                {
                    Kind = MediaKind.Image,
                    DownloadUrl = $"https://example.com/{i}.jpg"
                })
                .ToList()
        };

    [Fact]
    public void Duplicate_prompt_names_the_folder_and_files()
    {
        var text = DuplicateWindow.Describe("Descargas", ["one.jpg", "two.jpg"]);
        Assert.Contains("already in Descargas", text);
        Assert.Contains("one.jpg", text);
        Assert.Contains("Skip, keep both copies, or replace them?", text);
    }
}
