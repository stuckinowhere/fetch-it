using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.Tests;

public class ToolStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fetchit-stage-" + Guid.NewGuid().ToString("N"));
    private readonly string _dest;

    public ToolStagingTests()
    {
        _dest = Path.Combine(_root, "dest");
        Directory.CreateDirectory(_dest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private ToolStaging Stage(params string[] names)
    {
        var staging = ToolStaging.Create(Path.Combine(_root, "staging"));
        foreach (var name in names)
        {
            var path = Path.Combine(staging.Folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "new");
        }

        return staging;
    }

    [Fact]
    public void Moves_finished_files_and_leaves_unfinished_ones()
    {
        using var staging = Stage("a.jpg", Path.Combine("sub", "b.mp4"), "c.jpg.part", "d.mp4.ytdl", "e.temp");

        Assert.Equal(2, staging.MoveTo(_dest, DuplicateChoice.KeepBoth));

        Assert.True(File.Exists(Path.Combine(_dest, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(_dest, "sub", "b.mp4")));
        Assert.False(File.Exists(Path.Combine(_dest, "c.jpg.part")));
        Assert.False(File.Exists(Path.Combine(_dest, "d.mp4.ytdl")));
        Assert.False(File.Exists(Path.Combine(_dest, "e.temp")));
    }

    [Fact]
    public void Keep_both_never_touches_an_existing_file()
    {
        File.WriteAllText(Path.Combine(_dest, "a.jpg"), "old");
        using var staging = Stage("a.jpg");

        staging.MoveTo(_dest, DuplicateChoice.KeepBoth);

        Assert.Equal("old", File.ReadAllText(Path.Combine(_dest, "a.jpg")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dest, "a_2.jpg")));
    }

    [Fact]
    public void Overwrite_replaces_the_existing_file()
    {
        File.WriteAllText(Path.Combine(_dest, "a.jpg"), "old");
        using var staging = Stage("a.jpg");

        staging.MoveTo(_dest, DuplicateChoice.Overwrite);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_dest, "a.jpg")));
    }

    [Fact]
    public void Dispose_removes_the_folder_and_unfinished_files()
    {
        var staging = Stage("c.jpg.part");
        var folder = staging.Folder;

        staging.Dispose();

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Create_clears_only_old_leftovers()
    {
        var parent = Path.Combine(_root, "staging");
        var old = Path.Combine(parent, "old-run");
        var recent = Path.Combine(parent, "other-download");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(recent);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));

        using var staging = ToolStaging.Create(parent);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void Skip_leaves_files_that_are_already_saved()
    {
        File.WriteAllText(Path.Combine(_dest, "a.jpg"), "old");
        using var staging = Stage("a.jpg", "b.jpg");

        Assert.Equal(1, staging.MoveTo(_dest, DuplicateChoice.Skip));

        Assert.Equal("old", File.ReadAllText(Path.Combine(_dest, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(_dest, "b.jpg")));
    }

    [Theory]
    [InlineData("video.f395.mp4")]
    [InlineData("video.f251.webm")]
    [InlineData("video.temp.mp4")]
    [InlineData("video.mp4.part-Frag12")]
    public void Unmerged_video_streams_are_not_moved(string name)
    {
        using var staging = Stage(name, "video.mp4");

        Assert.Equal(1, staging.MoveTo(_dest, DuplicateChoice.KeepBoth));

        Assert.True(File.Exists(Path.Combine(_dest, "video.mp4")));
        Assert.False(File.Exists(Path.Combine(_dest, name)));
    }

    [Fact]
    public void Photo_names_with_dots_are_kept()
    {
        using var staging = Stage("trip.f2.jpg");

        Assert.Equal(1, staging.MoveTo(_dest, DuplicateChoice.KeepBoth));
    }
}
