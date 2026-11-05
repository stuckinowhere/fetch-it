using Avalonia;
using FetchIt.Services;

namespace FetchIt.Tests;

public class WindowStoreTests
{
    private static readonly PixelRect Main = new(0, 0, 1920, 1040);
    private static readonly PixelRect Left = new(-1920, 0, 1920, 1040);

    [Fact]
    public void A_spot_on_the_second_screen_is_kept()
        => Assert.True(WindowStore.IsReachable(new WindowSpot(-1500, 100, 1100, 700), [Main, Left]));

    [Fact]
    public void A_screen_that_is_gone_falls_back_to_centred()
        => Assert.False(WindowStore.IsReachable(new WindowSpot(-1500, 100, 1100, 700), [Main]));

    [Fact]
    public void A_title_bar_above_the_screen_is_not_reachable()
        => Assert.False(WindowStore.IsReachable(new WindowSpot(100, -50, 1100, 700), [Main]));

    [Fact]
    public void A_spot_survives_saving_and_loading()
    {
        var path = Path.Combine(Path.GetTempPath(), "fetchit-window-" + Guid.NewGuid().ToString("N"), "window.json");
        try
        {
            WindowStore.Save(new WindowSpot(-1500, 100, 1100, 700), path);

            Assert.Equal(new WindowSpot(-1500, 100, 1100, 700), WindowStore.Load(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void A_missing_or_broken_file_means_no_spot()
    {
        var path = Path.Combine(Path.GetTempPath(), "fetchit-window-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Null(WindowStore.Load(path));

        File.WriteAllText(path, "not json");
        try
        {
            Assert.Null(WindowStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
