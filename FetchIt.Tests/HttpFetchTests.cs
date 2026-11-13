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
        Assert.Contains("Chrome/142.0.0.0", http.DefaultRequestHeaders.UserAgent.ToString(), StringComparison.Ordinal);
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

    private static string TempFile()
    {
        var dest = Path.Combine(Path.GetTempPath(), "WasdFetchIt-tests", Guid.NewGuid().ToString("N") + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        return dest;
    }

    [Fact]
    public async Task A_silent_connection_is_cut_and_tried_again()
    {
        var dest = TempFile();
        var handler = new DripHandler(chunks: 1, pause: Timeout.InfiniteTimeSpan);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<IOException>(() => HttpFetch.SaveStreamAsync(
            http, "https://example.com/a.bin", dest, CancellationToken.None,
            retries: 2, retryDelayMs: 1, idleLimit: TimeSpan.FromMilliseconds(150)));

        Assert.Equal(HttpFetch.QuietMessage, error.Message);
        Assert.Equal(2, handler.Calls);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task A_slow_download_that_keeps_arriving_is_not_cut()
    {
        var dest = TempFile();
        var handler = new DripHandler(chunks: 6, pause: TimeSpan.FromMilliseconds(60));
        using var http = new HttpClient(handler);
        try
        {
            // Longer in all than the limit, but never silent for that long.
            await HttpFetch.SaveStreamAsync(
                http, "https://example.com/a.bin", dest, CancellationToken.None,
                retries: 1, idleLimit: TimeSpan.FromMilliseconds(250));

            Assert.Equal(6, new FileInfo(dest).Length);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            HttpFetch.TryDelete(dest);
        }
    }

    [Fact]
    public async Task Stop_is_not_mistaken_for_a_silent_connection()
    {
        var dest = TempFile();
        using var http = new HttpClient(new DripHandler(chunks: 1, pause: Timeout.InfiniteTimeSpan));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpFetch.SaveStreamAsync(
            http, "https://example.com/a.bin", dest, stop.Token, idleLimit: TimeSpan.FromSeconds(30)));
    }

    /// <summary>Sends one byte, waits, and repeats. With an endless pause it never ends.</summary>
    private sealed class DripHandler(int chunks, TimeSpan pause) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DripStream(chunks, pause))
            });
        }
    }

    private sealed class DripStream(int chunks, TimeSpan pause) : Stream
    {
        private int _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var endless = pause == Timeout.InfiniteTimeSpan;
            if (_sent >= chunks && !endless)
                return 0;
            if (_sent > 0)
                await Task.Delay(pause, cancellationToken);
            _sent++;
            buffer.Span[0] = 7;
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
