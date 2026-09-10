using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FetchIt.Models;

namespace FetchIt.ViewModels;

public sealed partial class PreviewCard : ObservableObject, IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
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
            using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
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

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Image?.Dispose();
        Image = null;
    }
}
