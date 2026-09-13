using FetchIt.Models;
using FetchIt.Services;
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

    [Fact]
    public void GoFile_timeout_asks_for_vpn()
    {
        Assert.Equal(8, MainViewModel.ProbeSeconds("https://gofile.io/d/Soimwa"));
        Assert.Equal(35, MainViewModel.ProbeSeconds("https://youtu.be/dQw4w9WgXcQ"));
        Assert.Equal(
            GofileService.UnreachableMessage,
            MainViewModel.ProbeTimeoutMessage("https://gofile.io/d/Soimwa"));
        Assert.Equal(
            "That site took too long. Try again.",
            MainViewModel.ProbeTimeoutMessage("https://youtu.be/dQw4w9WgXcQ"));
    }

    [Fact]
    public void Short_hides_json_parser_noise()
    {
        Assert.Equal(
            "Could not read that link.",
            MainViewModel.Short("'g' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0"));
    }

    [Fact]
    public async Task Fetch_without_link_shows_error_alert()
    {
        var ui = new FakeUi();
        var vm = new MainViewModel { Ui = ui, Url = "not-a-link" };
        await vm.FetchCommand.ExecuteAsync(null);
        Assert.Equal("Not a link.", vm.Error);
        Assert.Empty(vm.Success);
        Assert.Equal(("Error", "Not a link.", true), Assert.Single(ui.Alerts));
    }

    [Fact]
    public async Task Download_without_preview_shows_error_alert()
    {
        var ui = new FakeUi();
        var vm = new MainViewModel { Ui = ui };
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal("Fetch first.", vm.Error);
        Assert.Equal(("Error", "Fetch first.", true), Assert.Single(ui.Alerts));
    }

    private sealed class FakeUi : IUiHost
    {
        public List<(string Heading, string Message, bool IsError)> Alerts { get; } = [];

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> ReadClipboardAsync() => Task.FromResult<string?>(null);
        public Task<bool> SignInInstagramAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task ShowUpdateAsync(UpdateCheckResult result) => Task.CompletedTask;
        public Task ShowAlertAsync(string heading, string message, bool isError)
        {
            Alerts.Add((heading, message, isError));
            return Task.CompletedTask;
        }

        public Task<DuplicateChoice> AskIfAlreadySavedAsync(string folderLabel, IReadOnlyList<string> names)
            => Task.FromResult(DuplicateChoice.Skip);
    }
}
