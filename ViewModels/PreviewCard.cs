using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.ViewModels;

public sealed partial class PreviewCard : ObservableObject, IDisposable
{
    private static readonly HttpClient Http = HttpFetch.CreateClient(timeout: TimeSpan.FromSeconds(12));
    private static readonly SemaphoreSlim ImageThrottle = new(6);

    // Scrolling past a tile should not fetch its picture.
    private static readonly TimeSpan ScrollPause = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _cts;

    private readonly int _decodeWidth;
    private readonly bool _tile;
    private readonly string? _thumbnail;

    /// <param name="item">What to show.</param>
    /// <param name="tile">A small tile in a grid, which may hold thousands.</param>
    public PreviewCard(MediaItem item, bool tile = false)
    {
        _decodeWidth = tile ? 320 : 960;
        _tile = tile;
        _thumbnail = string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl;
        Title = item.Title;
        Overlay = item.Overlay;
        if (!tile)
            Show();
    }

    public string Title { get; }
    public string Overlay { get; }

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _tileWidth = 320;
    [ObservableProperty] private double _tileImageHeight = 180;

    internal bool IsLoading => _cts is not null;

    /// <summary>A profile can have thousands of tiles, so only those on screen hold a picture.</summary>
    public void Show()
    {
        if (_thumbnail is null || _cts is not null)
            return;
        _cts = new CancellationTokenSource();
        _ = LoadAsync(_thumbnail, _cts.Token);
    }

    public void Hide()
    {
        if (_tile)
            Dispose();
    }

    private async Task LoadAsync(string url, CancellationToken token)
    {
        try
        {
            if (_tile)
                await Task.Delay(ScrollPause, token).ConfigureAwait(false);
            await ImageThrottle.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
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
                    var bitmap = Decode(ms);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                            bitmap.Dispose();
                        else
                            Image = bitmap;
                    });
                    return;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        finally
        {
            ImageThrottle.Release();
        }
    }

    private Bitmap Decode(MemoryStream picture)
    {
        try
        {
            return Bitmap.DecodeToWidth(picture, _decodeWidth);
        }
        catch
        {
            picture.Position = 0;
            return new Bitmap(picture);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        var shown = Image;
        Image = null;
        shown?.Dispose();
    }
}
