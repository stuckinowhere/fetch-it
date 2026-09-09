using FetchIt.Models;

namespace FetchIt.Services;

public sealed class MediaFetcher
{
    private readonly YtDlpService _yt = new();
    private readonly GalleryDlService _gallery = new();

    public async Task<MediaProbe> ProbeAsync(string url, bool useCookies, CancellationToken cancellationToken)
    {
        if (!MediaRouter.TryParseHttpUrl(url, out var uri))
            throw new InvalidOperationException("Not a link.");

        var first = MediaRouter.Prefer(uri);
        try
        {
            return await RunProbe(first, url, useCookies, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            var second = first == EngineKind.YtDlp ? EngineKind.GalleryDl : EngineKind.YtDlp;
            return await RunProbe(second, url, useCookies, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DownloadAsync(
        MediaProbe probe,
        string url,
        string folder,
        VideoQuality quality,
        bool useCookies,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
        => probe.Engine == EngineKind.GalleryDl
            ? _gallery.DownloadAsync(url, folder, probe.Title, probe.FileCount, progress, useCookies, cancellationToken)
            : _yt.DownloadAsync(url, folder, probe.Title, quality, useCookies, probe.FileCount, progress, cancellationToken);

    private Task<MediaProbe> RunProbe(EngineKind engine, string url, bool useCookies, CancellationToken cancellationToken)
        => engine == EngineKind.GalleryDl
            ? _gallery.ProbeAsync(url, useCookies, cancellationToken)
            : _yt.ProbeAsync(url, useCookies, cancellationToken);
}
