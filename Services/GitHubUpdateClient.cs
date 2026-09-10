using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace FetchIt.Services;

public enum UpdateCheckStatus
{
    Current,
    Available,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version Current,
    Version? Latest,
    string? Tag,
    string? DownloadUrl,
    string? ReleaseUrl,
    string? Error);

public static class AppVersion
{
    public static Version Current
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            return Normalize(version);
        }
    }

    internal static Version Normalize(Version version) =>
        new(
            version.Major,
            version.Minor,
            version.Build < 0 ? 0 : version.Build,
            version.Revision < 0 ? 0 : version.Revision);

    internal static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var trimmed = tag.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..];

        if (!Version.TryParse(trimmed, out var parsed))
            return false;

        version = Normalize(parsed);
        return true;
    }
}

public sealed class GitHubUpdateClient : IDisposable
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/stuckinowhere/fetch-it/releases/latest";

    private readonly HttpClient _http;
    private readonly Version _current;
    private readonly bool _ownsHttp;

    public GitHubUpdateClient(HttpMessageHandler? handler = null, Version? current = null)
    {
        _current = AppVersion.Normalize(current ?? AppVersion.Current);
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(8) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(8) };
        _ownsHttp = true;

        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WasdFetchIt", _current.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUrl, cancellation).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    _current,
                    null,
                    null,
                    null,
                    null,
                    $"GitHub returned {(int)response.StatusCode}.");
            }

            return Parse(body, _current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                _current,
                null,
                null,
                null,
                null,
                "Could not reach GitHub Releases.");
        }
    }

    internal static UpdateCheckResult Parse(string json, Version current)
    {
        current = AppVersion.Normalize(current);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
        if (!AppVersion.TryParseTag(tag, out var latest))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                current,
                null,
                tag,
                null,
                null,
                "Latest release has no version tag.");
        }

        var releaseUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;
        string? setupUrl = null;
        string? zipUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;

                if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase))
                    setupUrl ??= url;
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    zipUrl ??= url;
            }
        }

        var download = setupUrl ?? zipUrl ?? releaseUrl;
        var available = latest > current;
        return new UpdateCheckResult(
            available ? UpdateCheckStatus.Available : UpdateCheckStatus.Current,
            current,
            latest,
            tag,
            download,
            releaseUrl,
            null);
    }

    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
