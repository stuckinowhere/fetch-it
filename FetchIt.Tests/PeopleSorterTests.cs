using FetchIt.Models;
using FetchIt.Services;
using SkiaSharp;

namespace FetchIt.Tests;

public sealed class PeopleSorterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fetchit-people-" + Guid.NewGuid().ToString("N"));
    private static readonly IProgress<FetchProgress> Quiet = new Progress<FetchProgress>();

    public PeopleSorterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private void Save(params string[] names)
    {
        foreach (var name in names)
            File.WriteAllText(Path.Combine(_folder, name), name);
    }

    private static Func<string, CancellationToken, Task<PeopleCheck>> Answers(Dictionary<string, PeopleCheck> byName)
        => (path, _) => Task.FromResult(byName[Path.GetFileName(path)]);

    [Fact]
    public async Task Only_files_without_people_move_and_nothing_is_deleted()
    {
        Save("person.jpg", "scenery.jpg", "broken.jpg");

        var outcome = await PeopleSorter.MoveAsync(
            _folder,
            ["person.jpg", "scenery.jpg", "broken.jpg"],
            Answers(new()
            {
                ["person.jpg"] = PeopleCheck.HasPeople,
                ["scenery.jpg"] = PeopleCheck.NoPeople,
                ["broken.jpg"] = PeopleCheck.Unknown
            }),
            Quiet,
            CancellationToken.None);

        Assert.Equal(new PeopleSorter.Outcome(1, 1), outcome);
        Assert.True(File.Exists(Path.Combine(_folder, "person.jpg")));
        Assert.True(File.Exists(Path.Combine(_folder, "broken.jpg")));
        Assert.False(File.Exists(Path.Combine(_folder, "scenery.jpg")));
        Assert.True(File.Exists(Path.Combine(_folder, L.WithoutPeople, "scenery.jpg")));
    }

    [Fact]
    public async Task Files_from_other_downloads_are_not_touched()
    {
        Save("new.jpg", "older.jpg");

        await PeopleSorter.MoveAsync(
            _folder,
            ["new.jpg"],
            (_, _) => Task.FromResult(PeopleCheck.NoPeople),
            Quiet,
            CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_folder, "older.jpg")));
    }

    [Fact]
    public async Task A_missing_file_is_skipped()
    {
        var outcome = await PeopleSorter.MoveAsync(
            _folder,
            ["gone.jpg"],
            (_, _) => throw new InvalidOperationException("should not be checked"),
            Quiet,
            CancellationToken.None);

        Assert.Equal(new PeopleSorter.Outcome(0, 0), outcome);
    }

    [Fact]
    public async Task Stop_ends_the_run()
    {
        Save("a.jpg");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => PeopleSorter.MoveAsync(
            _folder,
            ["a.jpg"],
            (_, _) => Task.FromResult(PeopleCheck.NoPeople),
            Quiet,
            cts.Token));

        Assert.True(File.Exists(Path.Combine(_folder, "a.jpg")));
    }

    [Fact]
    public void Video_frames_spread_across_the_clip()
    {
        Assert.Equal([1.0, 3.0, 5.0, 7.0, 9.0], PeopleSorter.FrameTimes(10).Select(t => Math.Round(t, 3)));
        Assert.Equal([0.0], PeopleSorter.FrameTimes(0));
    }

    [Fact]
    public void Score_is_the_best_person_box_and_ignores_other_classes()
    {
        var output = new float[PersonDetector.ValuesPerBox * 2];
        output[4] = 0.5f;
        output[5] = 0.4f;
        output[6] = 1f;
        output[PersonDetector.ValuesPerBox + 4] = 0.9f;
        output[PersonDetector.ValuesPerBox + 5] = 0.8f;

        Assert.Equal(0.72f, PersonDetector.BestPersonScore(output), 3);
    }

    [Fact]
    public void Input_is_blue_green_red_on_grey_padding_from_the_top_left()
    {
        using var bitmap = new SKBitmap(100, 50, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bitmap.Erase(new SKColor(10, 20, 30));

        var input = PersonDetector.ToInput(bitmap);

        var size = PersonDetector.InputSize;
        var area = size * size;
        Assert.Equal(area * 3, input.Length);
        Assert.Equal(30f, input[0], 1);
        Assert.Equal(20f, input[area], 1);
        Assert.Equal(10f, input[area * 2], 1);
        Assert.Equal(114f, input[area - 1], 1);
    }
}
