using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FetchIt.Models;

namespace FetchIt.ViewModels;

public sealed partial class PreviewCard : ObservableObject, IDisposable
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        return http;
    }
    private CancellationTokenSource? _cts = new();

    public PreviewCard(MediaItem item)
    {
        Item = item;
        Title = item.Title;
        Overlay = item.Overlay;
        IsVideo = item.Kind == MediaKind.Video;
        if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl))
            _ = LoadAsync(item.ThumbnailUrl, _cts.Token);
    }

    public MediaItem Item { get; }
    public string Title { get; }
    public string Overlay { get; }
    public bool IsVideo { get; }

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _tileWidth = 320;
    [ObservableProperty] private double _tileImageHeight = 180;

    private async Task LoadAsync(string url, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (NeedsXReferer(url))
                request.Headers.Referrer = new Uri("https://x.com/");
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms, token).ConfigureAwait(false);
            ms.Position = 0;
            var bitmap = await Dispatcher.UIThread.InvokeAsync(() => new Bitmap(ms));
            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            Image = bitmap;
        }
        catch
        {
        }
    }

    internal static bool NeedsXReferer(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host.Trim().ToLowerInvariant();
        if (host.StartsWith("www."))
            host = host[4..];
        return host is "pbs.twimg.com" or "video.twimg.com" or "twimg.com"
            or "x.com" or "twitter.com" or "mobile.twitter.com" or "mobile.x.com";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Image?.Dispose();
        Image = null;
    }
}
