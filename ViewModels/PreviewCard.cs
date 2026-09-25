using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.ViewModels;

public sealed partial class PreviewCard : ObservableObject, IDisposable
{
    private static readonly HttpClient Http = HttpFetch.CreateClient(timeout: TimeSpan.FromSeconds(12));
    private CancellationTokenSource? _cts = new();

    public PreviewCard(MediaItem item)
    {
        Title = item.Title;
        Overlay = item.Overlay;
        if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl))
            _ = LoadAsync(item.ThumbnailUrl, _cts.Token);
    }

    public string Title { get; }
    public string Overlay { get; }

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _tileWidth = 320;
    [ObservableProperty] private double _tileImageHeight = 180;

    private async Task LoadAsync(string url, CancellationToken token)
    {
        foreach (var candidate in ThumbnailUrl.Candidates(url))
        {
            if (!ThumbnailUrl.LooksLikeImage(candidate))
                continue;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                var referer = ThumbnailUrl.RefererFor(candidate);
                if (referer is not null)
                    request.Headers.Referrer = referer;
                using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("json", StringComparison.OrdinalIgnoreCase))
                    continue;
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms, token).ConfigureAwait(false);
                if (ms.Length < 32)
                    continue;
                ms.Position = 0;
                var bitmap = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        return Bitmap.DecodeToWidth(ms, 960);
                    }
                    catch
                    {
                        ms.Position = 0;
                        return new Bitmap(ms);
                    }
                });
                if (token.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }

                Image = bitmap;
                return;
            }
            catch
            {
            }
        }
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
