using System.Net;
using FetchIt.Services;

namespace FetchIt.Tests;

public class UpdateCheckTests
{
    private const string LatestJson = """
        {
          "tag_name": "v1.1.0",
          "html_url": "https://github.com/stuckinowhere/fetch-it/releases/tag/v1.1.0",
          "assets": [
            {
              "name": "fetch-it-v1.1.0-win-x64.zip",
              "browser_download_url": "https://example.test/fetch-it.zip"
            },
            {
              "name": "fetch-it-v1.1.0-win-x64-setup.exe",
              "browser_download_url": "https://example.test/fetch-it-setup.exe"
            }
          ]
        }
        """;

    [Theory]
    [InlineData("v1.1.0", 1, 1, 0)]
    [InlineData("1.1.0", 1, 1, 0)]
    [InlineData("v1.1.0.1", 1, 1, 0)]
    public void ParsesReleaseTags(string tag, int major, int minor, int build)
    {
        Assert.True(AppVersion.TryParseTag(tag, out var version));
        Assert.Equal(new Version(major, minor, build, tag == "v1.1.0.1" ? 1 : 0), version);
    }

    [Fact]
    public void ThreePartTagMatchesFourPartAssemblyVersion()
    {
        var fromTag = AppVersion.Normalize(new Version(1, 1, 0));
        var fromAssembly = new Version(1, 1, 0, 0);
        Assert.Equal(0, fromTag.CompareTo(fromAssembly));
    }

    [Fact]
    public void Parse_PrefersSetupExe_WhenNewer()
    {
        var result = GitHubUpdateClient.Parse(LatestJson, new Version(1, 0, 0, 0));

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(new Version(1, 1, 0, 0), result.Latest);
        Assert.Equal("https://example.test/fetch-it-setup.exe", result.DownloadUrl);
    }

    [Fact]
    public void Parse_SameTag_IsCurrent()
    {
        var result = GitHubUpdateClient.Parse(LatestJson, new Version(1, 1, 0, 0));
        Assert.Equal(UpdateCheckStatus.Current, result.Status);
    }

    [Fact]
    public void Parse_OlderGitHubTag_IsCurrent()
    {
        var json = """{ "tag_name": "v1.0.0", "html_url": "https://example.test/r", "assets": [] }""";
        var result = GitHubUpdateClient.Parse(json, new Version(1, 1, 0, 0));
        Assert.Equal(UpdateCheckStatus.Current, result.Status);
        Assert.Equal("https://example.test/r", result.DownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_ReadsLatestReleaseJson()
    {
        var handler = new StubHandler(LatestJson);
        using var client = new GitHubUpdateClient(handler, new Version(1, 0, 0, 0));

        var result = await client.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(GitHubUpdateClient.LatestReleaseUrl, handler.LastUrl);
        Assert.Contains("WasdFetchIt", handler.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_HttpError_IsFailed()
    {
        var handler = new StubHandler("nope", HttpStatusCode.NotFound);
        using var client = new GitHubUpdateClient(handler, new Version(1, 0, 0, 0));

        var result = await client.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("404", result.Error);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public string? LastUrl { get; private set; }

        public string UserAgent { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body)
            });
        }
    }
}
