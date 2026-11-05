using FetchIt.Models;
using FetchIt.Services;
using FetchIt.ViewModels;
using Xunit.Abstractions;

namespace FetchIt.Tests;

/// <summary>
/// Real network, real yt-dlp / ffmpeg / gallery-dl. Opt in with FETCHIT_E2E=1;
/// CI never sets it, so these are skipped there.
/// </summary>
public class EndToEndTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("FETCHIT_E2E") == "1";

    [Fact]
    public async Task Tools_install_including_standalone_gallery_dl()
    {
        if (!Enabled) return;

        var tools = await ToolBootstrapper.EnsureAsync(null, CancellationToken.None);
        output.WriteLine($"yt-dlp: {tools.YtDlp}");
        output.WriteLine($"gallery-dl: {tools.GalleryDl}");
        Assert.True(File.Exists(tools.YtDlp));
        Assert.NotNull(tools.GalleryDl);
        Assert.True(File.Exists(tools.GalleryDl));

        var version = await ProcessRunner.RunTextAsync(tools.GalleryDl!, ["--version"], CancellationToken.None, timeout: TimeSpan.FromSeconds(60));
        output.WriteLine($"gallery-dl --version: {version.Trim()}");
        Assert.StartsWith("1.", version.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_fetch_then_download_through_view_model()
    {
        if (!Enabled) return;

        var folder = Path.Combine(Path.GetTempPath(), "fetchit-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var ui = new RecordingUi();
            var vm = new MainViewModel { Ui = ui, FolderPath = folder };
            vm.Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw"; // "Me at the zoo", 19 s, public

            await vm.FetchCommand.ExecuteAsync(null);
            output.WriteLine($"title: {vm.Title} | summary: {vm.Summary} | alerts: {string.Join(" / ", ui.Alerts)}");
            Assert.Empty(ui.Alerts);
            Assert.True(vm.HasPreview);
            Assert.NotNull(vm.Probe);
            Assert.Equal(1, vm.Probe!.VideoCount);
            Assert.Contains("zoo", vm.Title, StringComparison.OrdinalIgnoreCase);

            await vm.DownloadCommand.ExecuteAsync(null);
            output.WriteLine($"status: {vm.StatusText} | error: {vm.StatusIsError} | alerts: {string.Join(" / ", ui.Alerts)}");
            Assert.Empty(ui.Alerts);
            Assert.True(vm.ShowStatus);
            Assert.False(vm.StatusIsError);
            Assert.True(vm.ShowOpenFolder);
            Assert.StartsWith("Saved to ", vm.StatusText, StringComparison.Ordinal);

            var files = Directory.GetFiles(folder);
            foreach (var file in files)
                output.WriteLine($"file: {Path.GetFileName(file)} {new FileInfo(file).Length:N0} bytes");
            var video = Assert.Single(files);
            Assert.EndsWith(".mp4", video, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(video).Length > 200_000);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [Fact]
    public async Task YouTube_channel_probes_as_flat_playlist_fast()
    {
        if (!Enabled) return;

        var fetcher = new MediaFetcher();
        var started = DateTime.UtcNow;
        var probe = await fetcher.ProbeAsync(
            "https://www.youtube.com/@jawed/videos",
            null,
            new CancellationTokenSource(TimeSpan.FromSeconds(35)).Token);
        var took = DateTime.UtcNow - started;
        output.WriteLine($"title: {probe.Title} | items: {probe.Items.Count} | took: {took.TotalSeconds:0.0}s");
        Assert.True(probe.Items.Count >= 1);
        Assert.All(probe.Items, item => Assert.Equal(MediaKind.Video, item.Kind));
        // Includes a cold SHA-256 pass over the tool binaries on first use.
        Assert.True(took < TimeSpan.FromSeconds(30), $"took {took.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task Remote_tool_pins_are_fetched_and_validated()
    {
        if (!Enabled) return;

        using var http = new HttpClient();
        var json = await http.GetByteArrayAsync(ToolPins.RemoteUrl);
        output.WriteLine(System.Text.Encoding.UTF8.GetString(json));
        string? sig = null;
        try
        {
            sig = await http.GetStringAsync(ToolPins.RemoteSignatureUrl);
        }
        catch (HttpRequestException ex)
        {
            output.WriteLine($"no signature on main yet: {ex.Message}");
        }

        // main may still carry an unsigned or older schema until this branch
        // lands; either outcome must be a clean decision, never a crash.
        if (sig is null || !ToolPins.VerifySignature(json, sig))
        {
            output.WriteLine("rejected: signature missing or invalid");
            return;
        }

        var table = ToolPinTable.Parse(System.Text.Encoding.UTF8.GetString(json));
        Assert.All(table.Downloads, pin => Assert.True(ToolPins.IsAllowedToolUrl(pin.Url)));
        output.WriteLine($"accepted: version {table.Version}, decision {ToolPins.Decide(ToolPins.Embedded, table)}");
    }

    private sealed class RecordingUi : IUiHost
    {
        public List<string> Alerts { get; } = [];
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> ReadClipboardAsync() => Task.FromResult<string?>(null);
        public Task<bool> SignInInstagramAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task ShowUpdateAsync(UpdateCheckResult result) => Task.CompletedTask;
        public Task ShowAlertAsync(string heading, string message, bool isError)
        {
            Alerts.Add($"{heading}: {message}");
            return Task.CompletedTask;
        }
        public Task<string?> AskQualityAsync(string title, IReadOnlyList<MediaQuality> qualities)
            => Task.FromResult<string?>(qualities.Count > 0 ? qualities[0].Url : null);
        public Task<bool?> AskIncludeVideosAsync(int images, int videos) => Task.FromResult<bool?>(true);
        public Task<bool> AskPasteLinkAsync(string link) => Task.FromResult(false);
        public Task ShowAccountAsync() => Task.CompletedTask;
    }
}
