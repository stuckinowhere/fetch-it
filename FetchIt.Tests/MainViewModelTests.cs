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
    public void Every_item_is_shown_from_the_start()
    {
        var vm = new MainViewModel();
        var items = Enumerable.Range(1, 120)
            .Select(i => new MediaItem { Title = $"Item {i}" })
            .ToList();
        var probe = new MediaProbe
        {
            Title = "Batch Test",
            ImageCount = 120,
            Items = items
        };

        vm.ApplyProbe(probe);
        Assert.Equal(120, vm.PreviewCards.Count);
        Assert.Contains("120", vm.Summary);
    }

    [Fact]
    public void Gallery_links_get_time_to_list_every_file()
    {
        Assert.Equal(1800, MainViewModel.ProbeSeconds("https://www.instagram.com/p/abc/"));
        Assert.Equal(35, MainViewModel.ProbeSeconds("https://www.youtube.com/watch?v=1"));
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
        Assert.Equal(("Error", "Not a link.", true), Assert.Single(ui.Alerts));
    }

    [Fact]
    public async Task Download_without_preview_shows_error_alert()
    {
        var ui = new FakeUi();
        var vm = new MainViewModel { Ui = ui };
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(("Error", "Fetch first.", true), Assert.Single(ui.Alerts));
    }

    private sealed class FakeUi : IUiHost
    {
        public List<(string Heading, string Message, bool IsError)> Alerts { get; } = [];
        public string? Clipboard { get; set; }
        public bool PasteAnswer { get; set; } = true;
        public int PasteAsks { get; private set; }

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> ReadClipboardAsync() => Task.FromResult(Clipboard);
        public Task<bool> SignInInstagramAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task ShowUpdateAsync(UpdateCheckResult result) => Task.CompletedTask;
        public int AccountOpens { get; private set; }
        public Task ShowAccountAsync() { AccountOpens++; return Task.CompletedTask; }
        public Task ShowAlertAsync(string heading, string message, bool isError)
        {
            Alerts.Add((heading, message, isError));
            return Task.CompletedTask;
        }

        public Task<string?> AskQualityAsync(string title, IReadOnlyList<MediaQuality> qualities)
            => Task.FromResult<string?>(qualities.Count > 0 ? qualities[0].Url : null);

        public bool? VideosAnswer { get; set; } = true;
        public int VideoAsks { get; private set; }

        public Task<bool?> AskIncludeVideosAsync(int images, int videos)
        {
            VideoAsks++;
            return Task.FromResult(VideosAnswer);
        }

        public Task<bool> AskPasteLinkAsync(string link)
        {
            PasteAsks++;
            return Task.FromResult(PasteAnswer);
        }
    }

    private const string ProfileLink = "https://www.instagram.com/someone/";

    private static MediaProbe Mixed(int images = 1, int videos = 1) => new()
    {
        Site = "Instagram",
        Engine = EngineKind.GalleryDl,
        ImageCount = images,
        VideoCount = videos,
        IsPartial = true,
        Items =
        [
            .. Enumerable.Range(0, images).Select(i => new MediaItem
            {
                Kind = MediaKind.Image,
                DownloadUrl = $"https://cdn.example/{i}.jpg",
                SaveName = $"{i}.jpg"
            }),
            .. Enumerable.Range(0, videos).Select(i => new MediaItem
            {
                Kind = MediaKind.Video,
                ToolUrl = $"https://www.instagram.com/p/{i}/",
                SaveName = $"{i}.mp4"
            })
        ]
    };

    [Fact]
    public async Task Photos_only_leaves_out_every_video()
    {
        var vm = new MainViewModel { Ui = new FakeUi { VideosAnswer = false } };

        var chosen = await vm.ChooseMediaAsync(Mixed(2, 3), ProfileLink);

        Assert.All(chosen!.Items, item => Assert.Equal(MediaKind.Image, item.Kind));
        Assert.Equal(2, chosen.Items.Count);
        Assert.Equal(2, chosen.FileCount);
        Assert.True(chosen.IsPartial);
        Assert.Empty(GalleryDlService.ToolPosts(@"C:\none", chosen, DuplicateChoice.Overwrite));
    }

    [Fact]
    public async Task Choosing_everything_keeps_the_list()
    {
        var probe = Mixed();
        var vm = new MainViewModel { Ui = new FakeUi { VideosAnswer = true } };

        Assert.Same(probe, await vm.ChooseMediaAsync(probe, ProfileLink));
    }

    [Fact]
    public async Task Cancelling_the_question_stops_the_download()
    {
        var vm = new MainViewModel { Ui = new FakeUi { VideosAnswer = null } };

        Assert.Null(await vm.ChooseMediaAsync(Mixed(), ProfileLink));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task One_kind_of_file_asks_nothing(int images, int videos)
    {
        var ui = new FakeUi();
        var vm = new MainViewModel { Ui = ui };

        await vm.ChooseMediaAsync(Mixed(images, videos), ProfileLink);

        Assert.Equal(0, ui.VideoAsks);
    }

    [Theory]
    [InlineData("https://gofile.io/d/abc")]
    [InlineData("https://ok.ru/video/123")]
    [InlineData("https://kemono.cr/fanbox/user/1")]
    public async Task Routes_that_ignore_the_list_ask_nothing(string url)
    {
        var ui = new FakeUi();
        var vm = new MainViewModel { Ui = ui };

        await vm.ChooseMediaAsync(Mixed(), url);

        Assert.Equal(0, ui.VideoAsks);
    }

    [Fact]
    public async Task A_list_only_the_tool_can_fetch_asks_nothing()
    {
        var ui = new FakeUi();
        var vm = new MainViewModel { Ui = ui };
        var probe = new MediaProbe
        {
            Engine = EngineKind.GalleryDl,
            ImageCount = 1,
            VideoCount = 1,
            Items = [new MediaItem { Kind = MediaKind.Image }, new MediaItem { Kind = MediaKind.Video }]
        };

        await vm.ChooseMediaAsync(probe, ProfileLink);

        Assert.Equal(0, ui.VideoAsks);
    }

    [Fact]
    public async Task PasteIfEmpty_asks_before_filling_url()
    {
        var ui = new FakeUi
        {
            Clipboard = "https://ok.ru/video/1",
            PasteAnswer = true
        };
        var vm = new MainViewModel { Ui = ui };
        await vm.PasteIfEmptyAsync();
        Assert.Equal(1, ui.PasteAsks);
        Assert.Equal("https://ok.ru/video/1", vm.Url);

        await vm.PasteIfEmptyAsync();
        Assert.Equal(1, ui.PasteAsks);
    }

    [Fact]
    public async Task PasteIfEmpty_skips_when_user_declines()
    {
        var ui = new FakeUi
        {
            Clipboard = "https://ok.ru/video/2",
            PasteAnswer = false
        };
        var vm = new MainViewModel { Ui = ui };
        await vm.PasteIfEmptyAsync();
        Assert.Equal(1, ui.PasteAsks);
        Assert.Equal("", vm.Url);
    }

    [Fact]
    public async Task Account_command_opens_the_account_window()
    {
        var ui = new FakeUi();
        var vm = new MainViewModel { Ui = ui };
        await vm.ShowAccountCommand.ExecuteAsync(null);
        Assert.Equal(1, ui.AccountOpens);
    }

    [Fact]
    public void ShortLink_truncates_long_urls()
    {
        var longUrl = "https://ok.ru/video/" + new string('a', 80);
        var shortLink = MainViewModel.ShortLink(longUrl);
        Assert.True(shortLink.Length <= 72);
        Assert.EndsWith("…", shortLink);
    }
}
