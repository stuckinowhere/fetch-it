using FetchIt.Models;
using FetchIt.ViewModels;

namespace FetchIt.Tests;

public class MainViewModelTests
{
    [Fact]
    public void FitPreview_fills_single_tile()
    {
        var vm = new MainViewModel();
        vm.FitPreview(900, 400);
        Assert.Equal(900, vm.TileWidth);
        Assert.Equal(400, vm.TileImageHeight);
    }

    [Fact]
    public void FitPreview_splits_wide_gallery()
    {
        var vm = new MainViewModel();
        vm.PreviewCards.Add(new PreviewCard(new MediaItem { Title = "a" }));
        vm.PreviewCards.Add(new PreviewCard(new MediaItem { Title = "b" }));
        vm.PreviewCards.Add(new PreviewCard(new MediaItem { Title = "c" }));
        vm.FitPreview(1200, 500);
        Assert.True(vm.TileWidth < 1200);
        Assert.Equal(vm.TileWidth, vm.PreviewCards[0].TileWidth);
    }
}
