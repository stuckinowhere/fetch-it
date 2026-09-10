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
    public void Duplicate_prompt_names_the_folder_and_files()
    {
        var text = DuplicateWindow.Describe("Descargas", ["one.jpg", "two.jpg"]);
        Assert.Contains("already in Descargas", text);
        Assert.Contains("one.jpg", text);
        Assert.Contains("Skip, keep both copies, or replace them?", text);
    }
}
