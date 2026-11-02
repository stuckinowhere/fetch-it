using FetchIt.Services;

namespace FetchIt.Tests;

public class UpdateInstallerTests
{
    [Theory]
    [InlineData("https://github.com/stuckinowhere/fetch-it/releases/download/v1.1.4/fetch-it-v1.1.4-win-x64-setup.exe", true)]
    [InlineData("https://github.com/stuckinowhere/fetch-it/releases/download/v1.1.4/fetch-it-v1.1.4-win-x64.zip", false)]
    [InlineData("https://github.com/stuckinowhere/fetch-it/releases/tag/v1.1.4", false)]
    [InlineData("https://evil.example/fetch-it-v1.1.4-win-x64-setup.exe", false)]
    public void IsSetupDownload_requires_github_setup_exe(string url, bool expected)
        => Assert.Equal(expected, UpdateInstaller.IsSetupDownload(url));

    [Fact]
    public void StagingPath_stays_under_temp()
    {
        var path = UpdateInstaller.StagingPath();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WasdFetchIt"));
        Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafePath_and_pe_check()
    {
        var dest = UpdateInstaller.StagingPath();
        File.WriteAllBytes(dest, "MZ"u8.ToArray().Concat(new byte[32]).ToArray());
        try
        {
            Assert.True(UpdateInstaller.IsSafeSetupPath(dest));
            Assert.True(UpdateInstaller.LooksLikePe(dest));
            Assert.False(UpdateInstaller.IsSafeSetupPath(Path.Combine(Path.GetTempPath(), "other.exe")));
            File.WriteAllBytes(dest, "PK"u8.ToArray());
            Assert.False(UpdateInstaller.LooksLikePe(dest));
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public void StartSetup_rejects_a_path_outside_temp()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"fetchit-not-safe-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(outside, "MZ"u8.ToArray().Concat(new byte[32]).ToArray());
        try
        {
            Assert.False(UpdateInstaller.StartSetup(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task DownloadSetup_writes_pe_from_allowed_url()
    {
        var payload = "MZ"u8.ToArray().Concat(new byte[64]).ToArray();
        var url = "https://github.com/stuckinowhere/fetch-it/releases/download/v9.0.0/fetch-it-v9.0.0-win-x64-setup.exe";
        using var installer = new UpdateInstaller(new BytesHandler(url, payload), minBytes: 2, maxBytes: 1024);
        var dest = await installer.DownloadSetupAsync(url, null, CancellationToken.None);
        try
        {
            Assert.Equal(UpdateInstaller.StagingPath(), dest);
            Assert.True(UpdateInstaller.LooksLikePe(dest));
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadSetup_rejects_tiny_or_non_pe()
    {
        var url = "https://github.com/stuckinowhere/fetch-it/releases/download/v9.0.0/fetch-it-v9.0.0-win-x64-setup.exe";
        using var tiny = new UpdateInstaller(new BytesHandler(url, "MZ"u8.ToArray()), minBytes: 16, maxBytes: 1024);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tiny.DownloadSetupAsync(url, null, CancellationToken.None));

        using var html = new UpdateInstaller(new BytesHandler(url, "<html>"u8.ToArray().Concat(new byte[32]).ToArray()), minBytes: 2, maxBytes: 1024);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            html.DownloadSetupAsync(url, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://github.com/stuckinowhere/fetch-it/releases/download/v1.1.4/fetch-it-v1.1.4-win-x64.zip", true)]
    [InlineData("https://github.com/stuckinowhere/fetch-it/releases/download/v1.1.4/fetch-it-v1.1.4-win-x64-setup.exe", false)]
    [InlineData("https://evil.example/file.zip", false)]
    public void IsZipDownload_requires_github_zip(string url, bool expected)
        => Assert.Equal(expected, UpdateInstaller.IsZipDownload(url));

    [Fact]
    public void IsSupportedDownload_accepts_both_setup_and_zip()
    {
        Assert.True(UpdateInstaller.IsSupportedDownload("https://github.com/stuckinowhere/fetch-it/releases/download/v1.0.0/setup.exe"));
        Assert.True(UpdateInstaller.IsSupportedDownload("https://github.com/stuckinowhere/fetch-it/releases/download/v1.0.0/app.zip"));
        Assert.False(UpdateInstaller.IsSupportedDownload("https://github.com/stuckinowhere/fetch-it/releases/tag/v1.0.0"));
    }

    [Fact]
    public void ApplyZip_extracts_files_correctly()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ZipTest_" + Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(temp);
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("sub/test.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("hello world");
            }

            UpdateInstaller.ApplyZip(zipPath, temp);
            var extracted = Path.Combine(temp, "sub", "test.txt");
            Assert.True(File.Exists(extracted));
            Assert.Equal("hello world", File.ReadAllText(extracted));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
            try { File.Delete(zipPath); } catch { }
        }
    }

    [Fact]
    public void ParseSums_reads_sha256sum_format()
    {
        var sums = UpdateInstaller.ParseSums(
            "# comment\n" +
            "ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789  fetch-it-v1.2.0-win-x64.zip\n" +
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *fetch-it-v1.2.0-win-x64-setup.exe\r\n" +
            "not a hash line\n");
        Assert.Equal(2, sums.Count);
        Assert.Equal(
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            UpdateInstaller.ExpectedSha256(sums, "https://github.com/x/y/releases/download/v1.2.0/fetch-it-v1.2.0-win-x64.zip"));
        Assert.Null(UpdateInstaller.ExpectedSha256(sums, "https://github.com/x/y/releases/download/v1.2.0/other.zip"));
    }

    [Fact]
    public async Task DownloadSetup_rejects_hash_mismatch()
    {
        var payload = "MZ"u8.ToArray().Concat(new byte[64]).ToArray();
        var url = "https://github.com/stuckinowhere/fetch-it/releases/download/v9.0.0/fetch-it-v9.0.0-win-x64-setup.exe";
        using var installer = new UpdateInstaller(new BytesHandler(url, payload), minBytes: 2, maxBytes: 1024);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.DownloadSetupAsync(url, null, CancellationToken.None, new string('0', 64)));
        Assert.Equal(UpdateInstaller.VerificationFailedMessage, ex.Message);
        Assert.False(File.Exists(UpdateInstaller.StagingPath() + ".part"));
    }

    [Fact]
    public async Task DownloadSetup_accepts_matching_hash()
    {
        var payload = "MZ"u8.ToArray().Concat(new byte[64]).ToArray();
        var hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        var url = "https://github.com/stuckinowhere/fetch-it/releases/download/v9.0.0/fetch-it-v9.0.0-win-x64-setup.exe";
        using var installer = new UpdateInstaller(new BytesHandler(url, payload), minBytes: 2, maxBytes: 1024);
        var dest = await installer.DownloadSetupAsync(url, null, CancellationToken.None, hex);
        try
        {
            Assert.True(File.Exists(dest));
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public void PlanOrder_prefers_setup_when_installed_with_setup()
    {
        var setup = "https://github.com/stuckinowhere/fetch-it/releases/download/v2.0.0/fetch-it-v2.0.0-win-x64-setup.exe";
        var zip = "https://github.com/stuckinowhere/fetch-it/releases/download/v2.0.0/fetch-it-v2.0.0-win-x64.zip";
        var result = new UpdateCheckResult(UpdateCheckStatus.Available, new Version(1, 0, 0, 0), new Version(2, 0, 0, 0), "v2.0.0", setup, null, null, zip);

        Assert.Equal([setup, zip], UpdateInstaller.PlanOrder(result, installedWithSetup: true));
        Assert.Equal([zip, setup], UpdateInstaller.PlanOrder(result, installedWithSetup: false));
    }

    [Fact]
    public async Task InstallAsync_requires_sums_asset()
    {
        var setup = "https://github.com/stuckinowhere/fetch-it/releases/download/v2.0.0/fetch-it-v2.0.0-win-x64-setup.exe";
        var result = new UpdateCheckResult(UpdateCheckStatus.Available, new Version(1, 0, 0, 0), new Version(2, 0, 0, 0), "v2.0.0", setup, null, null);
        using var installer = new UpdateInstaller(new BytesHandler(setup, "MZ"u8.ToArray()), minBytes: 1, maxBytes: 1024);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(result, null, CancellationToken.None));
        Assert.Equal(UpdateInstaller.NoSumsMessage, ex.Message);
    }

    [Fact]
    public void IsInstalledWithSetup_looks_for_inno_uninstaller()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fetchit-inno-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(UpdateInstaller.IsInstalledWithSetup(dir));
            File.WriteAllBytes(Path.Combine(dir, "unins000.dat"), [1]);
            Assert.True(UpdateInstaller.IsInstalledWithSetup(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class BytesHandler : HttpMessageHandler
    {
        private readonly string _url;
        private readonly byte[] _body;

        public BytesHandler(string url, byte[] body)
        {
            _url = url;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body),
                RequestMessage = request
            };
            request.RequestUri = new Uri(_url);
            return Task.FromResult(response);
        }
    }
}
