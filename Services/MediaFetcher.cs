using FetchIt.Models;

namespace FetchIt.Services;

public sealed class MediaFetcher
{
    private readonly YtDlpService _yt = new();
    private readonly GalleryDlService _gallery = new();
    private readonly GofileService _gofile = new();
    private readonly OkRuService _okru = new();
    private readonly KemonoService _kemono = new();

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

        if (MediaRouter.IsKemono(uri))
            return await _kemono.ProbeAsync(url, progress, cancellationToken).ConfigureAwait(false);

        if (MediaRouter.IsGofile(uri))
            return await _gofile.ProbeAsync(url, progress, cancellationToken).ConfigureAwait(false);

        if (MediaRouter.IsOkRu(uri))
            return await _okru.ProbeAsync(url, progress, cancellationToken).ConfigureAwait(false);

        if (MediaRouter.IsBunkr(uri) && !MediaRouter.IsBunkrFileOrAlbum(uri))
            throw new InvalidOperationException(MediaRouter.BunkrHomeMessage);

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
            || ex.Message == MediaRouter.InstagramSessionMessage
            || ex.Message.Contains("took too long", StringComparison.OrdinalIgnoreCase))
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

    public async Task<string?> DownloadAsync(
        MediaProbe probe,
        string url,
        string folder,
        IProgress<FetchProgress> progress,
        DuplicateChoice duplicate,
        CancellationToken cancellationToken)
    {
        var publicUrl = MediaRouter.CanonicalPublicUrl(url);
        if (MediaRouter.TryParseHttpUrl(publicUrl, out var uri) && MediaRouter.IsKemono(uri))
            return await _kemono.DownloadAsync(folder, probe, duplicate, progress, cancellationToken).ConfigureAwait(false);
        if (MediaRouter.TryParseHttpUrl(publicUrl, out uri) && MediaRouter.IsGofile(uri))
        {
            await DownloadGofileAsync(probe, url, folder, progress, duplicate, cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (MediaRouter.TryParseHttpUrl(publicUrl, out uri) && MediaRouter.IsOkRu(uri)
            && probe.Items.Any(i => !string.IsNullOrEmpty(i.DownloadUrl)))
        {
            await _okru.DownloadAsync(folder, probe, duplicate, progress, cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (probe.Engine == EngineKind.GalleryDl)
            await _gallery.DownloadAsync(publicUrl, folder, probe, duplicate, progress, cancellationToken).ConfigureAwait(false);
        else
            await _yt.DownloadAsync(publicUrl, folder, probe.Title, probe.FileCount, duplicate, progress, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task DownloadGofileAsync(
        MediaProbe probe,
        string url,
        string folder,
        IProgress<FetchProgress> progress,
        DuplicateChoice duplicate,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gofile.DownloadAsync(url, folder, probe, duplicate, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (IsFinalGofileError(ex))
        {
            throw;
        }
        catch
        {
            progress.Report(new FetchProgress { Status = "Trying another reader…" });
            await _gallery.DownloadAsync(url, folder, probe, duplicate, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static bool IsFinalGofileError(InvalidOperationException ex)
    {
        var message = ex.Message;
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Windows blocked", StringComparison.OrdinalIgnoreCase)
               || message == "Not a GoFile link."
               || message == GofileService.UnreachableMessage
               || message == GofileService.SslMessage
               || IsPartialSaveMessage(message);
    }

    internal static bool IsPartialSaveMessage(string message)
        => message.StartsWith("Saved ", StringComparison.Ordinal)
           && message.Contains(" of ", StringComparison.Ordinal);

    private Task<MediaProbe> RunProbe(
        EngineKind engine,
        string url,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
        => engine == EngineKind.GalleryDl
            ? _gallery.ProbeAsync(url, progress, cancellationToken)
            : _yt.ProbeAsync(url, progress, cancellationToken);
}
