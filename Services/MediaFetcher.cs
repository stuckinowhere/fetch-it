using FetchIt.Models;

namespace FetchIt.Services;

public sealed class MediaFetcher
{
    private readonly YtDlpService _yt = new();
    private readonly GalleryDlService _gallery = new();

    public async Task<MediaProbe> ProbeAsync(
        string url,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!MediaRouter.TryParseHttpUrl(url, out var uri))
            throw new InvalidOperationException("Not a link.");

        var publicUrl = MediaRouter.CanonicalPublicUrl(url);
        var original = url.Trim().TrimEnd('/');
        if (!string.Equals(publicUrl.TrimEnd('/'), original, StringComparison.OrdinalIgnoreCase))
            progress?.Report(new FetchProgress { Status = "Reading public profile…" });
        else
            progress?.Report(new FetchProgress { Status = "Reading link…" });

        var first = MediaRouter.Prefer(uri);
        try
        {
            return await RunProbe(first, publicUrl, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (
            ex.Message == MediaRouter.PublicOnlyMessage
            || ex.Message == MediaRouter.InstagramSessionMessage)
        {
            throw;
        }
        catch
        {
            var second = first == EngineKind.YtDlp ? EngineKind.GalleryDl : EngineKind.YtDlp;
            progress?.Report(new FetchProgress { Status = "Trying another reader…" });
            return await RunProbe(second, publicUrl, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DownloadAsync(
        MediaProbe probe,
        string url,
        string folder,
        IProgress<FetchProgress> progress,
        DuplicateChoice duplicate,
        CancellationToken cancellationToken)
    {
        var publicUrl = MediaRouter.CanonicalPublicUrl(url);
        return probe.Engine == EngineKind.GalleryDl
            ? _gallery.DownloadAsync(publicUrl, folder, probe, duplicate, progress, cancellationToken)
            : _yt.DownloadAsync(publicUrl, folder, probe.Title, probe.FileCount, duplicate, progress, cancellationToken);
    }

    private Task<MediaProbe> RunProbe(
        EngineKind engine,
        string url,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
        => engine == EngineKind.GalleryDl
            ? _gallery.ProbeAsync(url, progress, cancellationToken)
            : _yt.ProbeAsync(url, progress, cancellationToken);
}
