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
