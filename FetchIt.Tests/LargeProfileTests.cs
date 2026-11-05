using FetchIt.Models;
using FetchIt.Services;
using FetchIt.ViewModels;

namespace FetchIt.Tests;

public class LargeProfileTests
{
    [Fact]
    public void Listing_counts_files_and_ignores_log_lines()
    {
        var listing = new ProfileListing();

        listing.Read(@"C:\staging\a.jpg");
        listing.Read("[instagram][info] Requesting page");
        listing.Read(@"C:\staging\b.mp4");

        Assert.Equal(2, listing.Files);
        Assert.Contains("2", listing.Status(TimeSpan.FromSeconds(75)));
    }

    [Fact]
    public void Listing_shows_a_rate_limit_wait_until_the_next_file()
    {
        var listing = new ProfileListing();

        listing.Read("[instagram][info] Waiting until 12:34:56 (rate limit)");
        Assert.Contains("12:34:56", listing.Status(TimeSpan.Zero));

        listing.Read(@"C:\staging\a.jpg");
        Assert.DoesNotContain("12:34:56", listing.Status(TimeSpan.Zero));
    }

    [Fact]
    public void Listing_clock_keeps_counting_past_an_hour()
        => Assert.Contains("75:03", new ProfileListing().Status(new TimeSpan(1, 15, 3)));

    [Fact]
    public void Listing_stalls_only_after_the_limit_with_no_output()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var listing = new ProfileListing(() => now);

        now = now.AddMinutes(19);
        Assert.False(listing.IsStalled(TimeSpan.FromMinutes(20)));

        listing.Read("[instagram][info] Waiting until 12:40:00 (rate limit)");
        now = now.AddMinutes(19);
        Assert.False(listing.IsStalled(TimeSpan.FromMinutes(20)));

        now = now.AddMinutes(2);
        Assert.True(listing.IsStalled(TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public void Listing_keeps_errors_to_explain_an_empty_run()
    {
        var listing = new ProfileListing();

        listing.Read("[instagram][error] HTTP redirect to login page");

        Assert.Contains("login page", listing.Problems);
        Assert.Equal(0, listing.Files);
    }

    [Fact]
    public void Lines_use_the_real_file_link_and_drop_a_cut_off_last_line()
    {
        string[] lines =
        [
            """{"_url":"https://cdn.example/a.jpg","filename":"a_n","extension":"jpg","category":"instagram"}""",
            """{"_url":"ytdl:https://www.instagram.com/p/abc/","filename":"b","extension":"mp4","category":"instagram","display_url":"https://cdn.example/b.jpg"}""",
            """{"_url":"https://cdn.example/a.jpg","filename":"a_n","extension":"jpg","category":"instagram"}""",
            """{"_url":"https://cdn.exa"""
        ];

        var probe = GalleryDlParser.ParseLines(lines, partial: true);

        Assert.True(probe.IsPartial);
        Assert.Equal(1, probe.ImageCount);
        Assert.Equal(1, probe.VideoCount);
        Assert.Equal("https://cdn.example/a.jpg", probe.Items[0].DownloadUrl);
        Assert.Equal("https://www.instagram.com/p/abc/", probe.Items[1].ToolUrl);
        Assert.Equal("b.mp4", probe.Items[1].SaveName);
        Assert.Equal("https://cdn.example/b.jpg", probe.Items[1].ThumbnailUrl);
    }

    [Fact]
    public void Long_captions_are_cut_to_keep_big_lists_small()
    {
        var caption = new string('x', 5000);
        string[] lines = [$$"""{"_url":"https://cdn.example/a.jpg","filename":"a","extension":"jpg","description":"{{caption}}"}"""];

        var probe = GalleryDlParser.ParseLines(lines, partial: false);

        Assert.False(probe.IsPartial);
        Assert.True(probe.Items[0].Title.Length <= GalleryDlParser.MaxListedTitle);
    }

    [Fact]
    public void A_partial_list_says_so_in_the_summary()
    {
        var probe = new MediaProbe { Site = "Instagram", ImageCount = 2, IsPartial = true };

        Assert.Contains(L.T("Partial list"), probe.Summary);
    }

    [Fact]
    public void Whole_profiles_have_no_time_limit_but_other_links_do()
    {
        Assert.True(MainViewModel.ProbeSeconds("https://www.instagram.com/someone/") > 86400);
        Assert.Equal(1800, MainViewModel.ProbeSeconds("https://www.instagram.com/p/abc/"));
    }

    [Theory]
    [InlineData("https://www.instagram.com/someone/", @"D:\Saved\someone")]
    [InlineData("https://www.instagram.com/stories/someone/", @"D:\Saved\someone")]
    [InlineData("https://www.instagram.com/some_one_/?hl=es", @"D:\Saved\some_one_")]
    [InlineData("https://www.instagram.com/p/abc/", @"D:\Saved")]
    [InlineData("https://www.youtube.com/watch?v=abc", @"D:\Saved")]
    public void A_whole_profile_gets_its_own_folder(string url, string expected)
        => Assert.Equal(expected, MainViewModel.SaveFolderFor(@"D:\Saved", url));

    [Theory]
    [InlineData("https://www.instagram.com/con/")]
    [InlineData("https://www.instagram.com/%2e%2e%2fother/")]
    [InlineData("https://www.instagram.com/..%5cother/")]
    public void A_strange_profile_name_stays_inside_the_save_folder(string url)
    {
        var folder = MainViewModel.SaveFolderFor(@"D:\Saved", url);

        Assert.StartsWith(@"D:\Saved\", Path.GetFullPath(folder));
    }

    [Fact]
    public void A_new_profile_folder_holds_no_files_yet()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fetchit-profile-" + Guid.NewGuid().ToString("N"));
        var probe = new MediaProbe
        {
            Items = [new MediaItem { Kind = MediaKind.Image, SaveName = "a.jpg" }]
        };

        Assert.Empty(SaveClash.ExistingNames(folder, probe));
    }

    [Fact]
    public void Listing_asks_gallery_dl_for_one_small_line_per_file()
    {
        var option = GalleryDlService.ListingOption(@"C:\staging\x");

        Assert.StartsWith("postprocessors=[", option);
        Assert.Contains("\"event\":\"prepare\"", option);
        Assert.Contains("C:\\\\staging\\\\x", option);
        Assert.Contains("\"_url\"", option);
        Assert.DoesNotContain("cookies", option, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rows_hold_the_columns_and_keep_every_card()
    {
        var cards = Enumerable.Range(0, 7)
            .Select(i => new PreviewCard(new MediaItem { Kind = MediaKind.Image, Title = $"{i}" }, tile: true))
            .ToList();

        var rows = PreviewRow.Split(cards, 3);

        Assert.Equal([3, 3, 1], rows.Select(row => row.Cards.Count));
        Assert.Equal(cards, rows.SelectMany(row => row.Cards));
    }

    [Fact]
    public void A_tile_does_not_load_until_it_is_on_screen()
    {
        var card = new PreviewCard(
            new MediaItem { Kind = MediaKind.Image, ThumbnailUrl = "https://cdn.example/a.jpg" },
            tile: true);

        Assert.False(card.IsLoading);
        card.Show();
        Assert.True(card.IsLoading);
        card.Hide();
        Assert.False(card.IsLoading);
        card.Dispose();
    }
}
