using System.Net;
using System.Net.Http;
using FetchIt.Services;

namespace FetchIt.Tests;

public class HttpFetchTests
{
    [Fact]
    public void CreateClient_sends_chrome_user_agent()
    {
        using var http = HttpFetch.CreateClient(timeout: TimeSpan.FromSeconds(1));
        Assert.Contains("Chrome/131.0.0.0", http.DefaultRequestHeaders.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveStreamAsync_writes_file_with_referer()
    {
        var dest = Path.Combine(Path.GetTempPath(), "WasdFetchIt-tests", Guid.NewGuid().ToString("N") + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var handler = new StubHandler(new byte[] { 1, 2, 3, 4 });
        using var http = new HttpClient(handler);
        try
        {
            await HttpFetch.SaveStreamAsync(
                http,
                "https://okcdn.ru/clip.bin",
                dest,
                CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(dest));
            Assert.Equal(new Uri("https://ok.ru/"), handler.Referrer);
            Assert.False(File.Exists(dest + ".part"));
        }
        finally
        {
            if (File.Exists(dest))
                File.Delete(dest);
        }
    }

    [Fact]
    public async Task SaveStreamAsync_resumes_from_part_file()
    {
        var dest = Path.Combine(Path.GetTempPath(), "WasdFetchIt-tests", Guid.NewGuid().ToString("N") + ".bin");
        var part = dest + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(part, [1, 2]);
        var handler = new StubHandler([3, 4], HttpStatusCode.PartialContent);
        using var http = new HttpClient(handler);
        try
        {
            await HttpFetch.SaveStreamAsync(
                http,
                "https://example.com/clip.bin",
                dest,
                CancellationToken.None,
                resume: true);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(dest));
            Assert.Equal(2, handler.RangeFrom);
        }
        finally
        {
            if (File.Exists(dest))
                File.Delete(dest);
            if (File.Exists(part))
                File.Delete(part);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly HttpStatusCode _status;

        public StubHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public Uri? Referrer { get; private set; }
        public long? RangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Referrer = request.Headers.Referrer;
            RangeFrom = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_body)
            });
        }
    }
}
