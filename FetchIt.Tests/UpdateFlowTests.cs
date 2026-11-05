using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using FetchIt.Services;

namespace FetchIt.Tests;

/// <summary>
/// End-to-end updater flow with a fake GitHub and a fake process/shutdown host.
/// Guards the v1.1.17 / v1.2.1 bug: the install steps run on a pool thread and
/// shutdown must go through the host, never fall through to a second asset.
/// </summary>
public class UpdateFlowTests : IDisposable
{
    private const string Base = "https://github.com/stuckinowhere/fetch-it/releases/download/v9.9.9/";
    private const string SetupUrl = Base + "fetch-it-v9.9.9-win-x64-setup.exe";
    private const string ZipUrl = Base + "fetch-it-v9.9.9-win-x64.zip";
    private const string SumsUrl = Base + "SHA256SUMS.txt";

    private readonly string _target = Path.Combine(Path.GetTempPath(), "fetchit-update-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _setup = "MZ"u8.ToArray().Concat(new byte[64]).ToArray();
    private readonly byte[] _zip;

    public UpdateFlowTests()
    {
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "FetchIt.exe"), "old");
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(archive.CreateEntry("FetchIt.exe").Open()))
                w.Write("new");
            using (var w = new StreamWriter(archive.CreateEntry("gallery-dl.exe").Open()))
                w.Write("gdl");
        }
        _zip = ms.ToArray();
    }

    public void Dispose()
    {
        try { Directory.Delete(_target, true); } catch { }
        try { File.Delete(UpdateInstaller.StagingPath()); } catch { }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private string Sums(byte[]? setupOverride = null, byte[]? zipOverride = null)
        => $"{Hex(setupOverride ?? _setup)}  fetch-it-v9.9.9-win-x64-setup.exe\n{Hex(zipOverride ?? _zip)}  fetch-it-v9.9.9-win-x64.zip\n";

    private static UpdateCheckResult Result() => new(
        UpdateCheckStatus.Available, new Version(1, 0, 0, 0), new Version(9, 9, 9, 0), "v9.9.9",
        SetupUrl, null, null, ZipUrl, SumsUrl);

    private sealed class Recorder
    {
        public List<ProcessStartInfo> Launched { get; } = [];
        public int Shutdowns;
        public int ShutdownThread = -1;
        public bool LaunchSucceeds { get; set; } = true;

        public UpdateHost Host(string target) => new()
        {
            TargetDirectory = target,
            Launch = start => { Launched.Add(start); return LaunchSucceeds; },
            LaunchSetup = start => { Launched.Add(start); return Task.FromResult(LaunchSucceeds); },
            RequestShutdown = () => { Interlocked.Increment(ref Shutdowns); ShutdownThread = Environment.CurrentManagedThreadId; }
        };
    }

    private UpdateInstaller Installer(FakeGitHub github, Recorder recorder)
        => new(github, minBytes: 2, maxBytes: 1024 * 1024, host: recorder.Host(_target));

    [Fact]
    public async Task Setup_install_launches_setup_requests_shutdown_and_never_touches_the_zip()
    {
        File.WriteAllBytes(Path.Combine(_target, "unins000.dat"), [1]);
        var github = new FakeGitHub(new() { [SumsUrl] = System.Text.Encoding.UTF8.GetBytes(Sums()), [SetupUrl] = _setup, [ZipUrl] = _zip });
        var recorder = new Recorder();
        using var installer = Installer(github, recorder);

        // Run off any UI context, exactly like the real ConfigureAwait(false) chain.
        await Task.Run(() => installer.InstallAsync(Result(), null, CancellationToken.None));

        var launch = Assert.Single(recorder.Launched);
        Assert.Equal(UpdateInstaller.StagingPath(), launch.FileName);
        Assert.Equal(UpdateInstaller.SilentArgs, launch.Arguments);
        Assert.Equal(1, recorder.Shutdowns);
        Assert.DoesNotContain(ZipUrl, github.Requested);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_target, "FetchIt.exe")));
    }

    [Fact]
    public async Task Setup_blocked_on_launch_falls_back_to_the_zip_and_restarts()
    {
        File.WriteAllBytes(Path.Combine(_target, "unins000.dat"), [1]);
        var github = new FakeGitHub(new() { [SumsUrl] = System.Text.Encoding.UTF8.GetBytes(Sums()), [SetupUrl] = _setup, [ZipUrl] = _zip });
        var recorder = new Recorder();
        var host = new UpdateHost
        {
            TargetDirectory = _target,
            // Setup is stopped at launch (a Defender ASR rule, say); the restart works.
            LaunchSetup = start => { recorder.Launched.Add(start); return Task.FromResult(false); },
            Launch = start => { recorder.Launched.Add(start); return true; },
            RequestShutdown = () => Interlocked.Increment(ref recorder.Shutdowns)
        };
        using var installer = new UpdateInstaller(github, minBytes: 2, maxBytes: 1024 * 1024, host: host);

        await Task.Run(() => installer.InstallAsync(Result(), null, CancellationToken.None));

        Assert.Equal(2, recorder.Launched.Count);
        Assert.Equal(Path.Combine(_target, "FetchIt.exe"), recorder.Launched[1].FileName);
        Assert.Equal(1, recorder.Shutdowns);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_target, "FetchIt.exe")));
    }

    [Fact]
    public async Task Zip_install_restarts_once_and_does_not_download_setup()
    {
        var github = new FakeGitHub(new() { [SumsUrl] = System.Text.Encoding.UTF8.GetBytes(Sums()), [SetupUrl] = _setup, [ZipUrl] = _zip });
        var recorder = new Recorder();
        using var installer = Installer(github, recorder);

        await Task.Run(() => installer.InstallAsync(Result(), null, CancellationToken.None));

        var launch = Assert.Single(recorder.Launched);
        Assert.Equal(Path.Combine(_target, "FetchIt.exe"), launch.FileName);
        Assert.Equal(1, recorder.Shutdowns);
        Assert.Equal("gdl", File.ReadAllText(Path.Combine(_target, "gallery-dl.exe")));
        Assert.DoesNotContain(SetupUrl, github.Requested);
    }

    [Fact]
    public async Task Zip_applied_but_restart_fails_is_final_and_never_runs_setup()
    {
        var github = new FakeGitHub(new() { [SumsUrl] = System.Text.Encoding.UTF8.GetBytes(Sums()), [SetupUrl] = _setup, [ZipUrl] = _zip });
        var recorder = new Recorder { LaunchSucceeds = false };
        using var installer = Installer(github, recorder);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Task.Run(() => installer.InstallAsync(Result(), null, CancellationToken.None)));

        Assert.Equal(UpdateInstaller.RestartManuallyMessage, ex.Message);
        Assert.Equal(0, recorder.Shutdowns);
        Assert.DoesNotContain(SetupUrl, github.Requested);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_target, "FetchIt.exe")));
    }

    [Fact]
    public async Task Tampered_setup_stops_everything()
    {
        File.WriteAllBytes(Path.Combine(_target, "unins000.dat"), [1]);
        var sums = Sums(setupOverride: "MZ-other-bytes"u8.ToArray());
        var github = new FakeGitHub(new() { [SumsUrl] = System.Text.Encoding.UTF8.GetBytes(sums), [SetupUrl] = _setup, [ZipUrl] = _zip });
        var recorder = new Recorder();
        using var installer = Installer(github, recorder);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(Result(), null, CancellationToken.None));

        Assert.Equal(UpdateInstaller.VerificationFailedMessage, ex.Message);
        Assert.Empty(recorder.Launched);
        Assert.Equal(0, recorder.Shutdowns);
        Assert.DoesNotContain(ZipUrl, github.Requested);
    }

    [Fact]
    public async Task Setup_download_failure_falls_back_to_zip()
    {
        File.WriteAllBytes(Path.Combine(_target, "unins000.dat"), [1]);
        var github = new FakeGitHub(new() { [SumsUrl] = System.Text.Encoding.UTF8.GetBytes(Sums()), [ZipUrl] = _zip });
        var recorder = new Recorder();
        using var installer = Installer(github, recorder);

        await Task.Run(() => installer.InstallAsync(Result(), null, CancellationToken.None));

        var launch = Assert.Single(recorder.Launched);
        Assert.Equal(Path.Combine(_target, "FetchIt.exe"), launch.FileName);
        Assert.Equal(1, recorder.Shutdowns);
    }

    [Theory]
    [InlineData("exit 1", false)] // What a Defender-blocked Inno bootstrap does.
    [InlineData("exit 0", true)]
    [InlineData("ping -n 6 127.0.0.1 > nul", true)] // Still running after the grace period.
    public async Task WatchSetup_judges_the_real_process(string command, bool expected)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c {command}") { UseShellExecute = false, CreateNoWindow = true };
        Assert.Equal(expected, await UpdateHost.WatchSetupAsync(start, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task WatchSetup_is_false_when_the_file_cannot_start()
    {
        var start = new ProcessStartInfo(Path.Combine(_target, "missing.exe")) { UseShellExecute = false };
        Assert.False(await UpdateHost.WatchSetupAsync(start, TimeSpan.FromSeconds(1)));
    }

    private sealed class FakeGitHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requested)
                Requested.Add(url);
            var response = files.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
