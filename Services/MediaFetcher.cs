using FetchIt.Models;

namespace FetchIt.Services;

public sealed class MediaFetcher
{
    private readonly YtDlpService _yt = new();
    private readonly GalleryDlService _gallery = new();
    private readonly GofileService _gofile = new();

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

        if (MediaRouter.IsGofile(uri))
            return await _gofile.ProbeAsync(url, progress, cancellationToken).ConfigureAwait(false);

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
            if (MediaRouter.IsGalleryHost(uri))
                throw;
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
        if (MediaRouter.TryParseHttpUrl(publicUrl, out var uri) && MediaRouter.IsGofile(uri))
            return DownloadGofileAsync(url, publicUrl, folder, probe, duplicate, progress, cancellationToken);
        return probe.Engine == EngineKind.GalleryDl
            ? _gallery.DownloadAsync(publicUrl, folder, probe, duplicate, progress, cancellationToken)
            : _yt.DownloadAsync(publicUrl, folder, probe.Title, probe.FileCount, duplicate, progress, cancellationToken);
    }

    private async Task DownloadGofileAsync(
        string url,
        string publicUrl,
        string folder,
        MediaProbe probe,
        DuplicateChoice duplicate,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gofile.DownloadAsync(url, folder, probe, duplicate, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("blocked that folder", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Windows blocked", StringComparison.OrdinalIgnoreCase)
            || ex.Message == "Not a GoFile link.")
        {
            throw;
        }
        catch
        {
            progress.Report(new FetchProgress { Status = "Trying another reader…" });
            await _yt.DownloadAsync(publicUrl, folder, probe.Title, probe.FileCount, duplicate, progress, cancellationToken)
                .ConfigureAwait(false);
        }
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
