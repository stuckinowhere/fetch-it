using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FetchIt.Models;

namespace FetchIt.Services;

public sealed class GofileService
{
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string Lang = "en-US";
    private const string TokenSalt = "5d4f7g8sd45fsd";
    private const long TokenWindow = 14400;
    internal const string SslMessage = "GoFile dropped the connection. Try Download again.";

    public async Task<MediaProbe> ProbeAsync(
        string url,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryParseFolder(url, out var id, out var password))
            throw new InvalidOperationException("Not a GoFile link.");

        progress?.Report(new FetchProgress { Status = "Reading folder…" });
        try
        {
            var folder = await ListFolderAsync(id, password, cancellationToken).ConfigureAwait(false);
            if (folder.Items.Count == 0)
                throw new InvalidOperationException(
                    folder.NeedsPassword ? "That folder needs a password." : "Could not read that link.");
            return folder.Probe;
        }
        catch (Exception ex) when (LooksLikeSsl(ex))
        {
            throw new InvalidOperationException(SslMessage);
        }
    }

    public async Task DownloadAsync(
        string url,
        string folder,
        MediaProbe probe,
        DuplicateChoice duplicate,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var dest = Path.GetFullPath(folder);
        if (!FolderStore.CanWrite(dest))
            throw new InvalidOperationException("Windows blocked that folder. Pick another save folder.");
        if (!TryParseFolder(url, out var id, out var password))
            throw new InvalidOperationException("Not a GoFile link.");

        var ready = probe.Items.Count > 0
                    && probe.Items.All(item => MediaRouter.TryParseHttpUrl(item.DownloadUrl, out _));
        MediaProbe fresh;
        string token;
        try
        {
            if (ready)
            {
                token = await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
                fresh = probe;
            }
            else
            {
                progress.Report(new FetchProgress { Status = "Reading folder…" });
                var listed = await ListFolderAsync(id, password, cancellationToken).ConfigureAwait(false);
                token = listed.Token;
                fresh = listed.Items.Count > 0 ? listed.Probe : probe;
            }
        }
        catch (Exception ex) when (LooksLikeSsl(ex))
        {
            throw new InvalidOperationException(SslMessage);
        }

        var jobs = GalleryDlService.PlanDirectFiles(dest, fresh, duplicate);
        if (jobs.Count == 0)
            throw new InvalidOperationException("Could not save those files.");

        using var handler = CreateHandler(cookies: true);
        if (!string.IsNullOrEmpty(token))
            handler.CookieContainer!.Add(new Cookie("accountToken", token, "/", ".gofile.io"));

        using var http = CreateClient(handler, TimeSpan.FromMinutes(2));
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);

        try
        {
            await GalleryDlService.DownloadDirectAsync(jobs, progress, cancellationToken, http).ConfigureAwait(false);
        }
        catch (Exception ex) when (LooksLikeSsl(ex))
        {
            throw new InvalidOperationException(SslMessage);
        }
    }

    internal static bool TryParseFolder(string url, out string id, out string? password)
    {
        id = "";
        password = null;
        if (!MediaRouter.TryParseHttpUrl(url, out var uri) || !MediaRouter.IsGofile(uri))
            return false;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("d", StringComparison.OrdinalIgnoreCase))
            return false;
        id = parts[1];
        if (id.Length == 0)
            return false;

        if (uri.Fragment.Length > 1)
            password = Uri.UnescapeDataString(uri.Fragment[1..]);
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2
                && Uri.UnescapeDataString(kv[0]).Equals("password", StringComparison.OrdinalIgnoreCase)
                && kv[1].Length > 0)
            {
                password = Uri.UnescapeDataString(kv[1]);
            }
        }

        return true;
    }

    internal static string WebsiteToken(string userAgent, string apiToken, long unixSeconds)
    {
        var slot = unixSeconds / TokenWindow;
        var data = $"{userAgent}::{Lang}::{apiToken}::{slot}::{TokenSalt}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    internal static MediaProbe ParseFolder(JsonElement data)
    {
        var title = ReadString(data, "name") ?? ReadString(data, "code") ?? "gofile";
        var items = new List<MediaItem>();
        if (data.TryGetProperty("children", out var children))
        {
            if (children.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in children.EnumerateObject())
                    TryAddFile(child.Value, items);
            }
            else if (children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                    TryAddFile(child, items);
            }
        }

        return new MediaProbe
        {
            Title = title,
            Site = "GoFile",
            Engine = EngineKind.GalleryDl,
            VideoCount = items.Count(item => item.Kind == MediaKind.Video),
            ImageCount = items.Count(item => item.Kind == MediaKind.Image),
            Items = items
        };
    }

    private sealed record ListedFolder(MediaProbe Probe, IReadOnlyList<MediaItem> Items, string Token, bool NeedsPassword);

    private async Task<ListedFolder> ListFolderAsync(
        string id,
        string? password,
        CancellationToken cancellationToken)
    {
        var token = await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FetchFolderAsync(id, password, token, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            ClearToken();
            token = await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
            return await FetchFolderAsync(id, password, token, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ListedFolder> FetchFolderAsync(
        string id,
        string? password,
        string token,
        CancellationToken cancellationToken)
    {
        using var http = NewClient();
        var data = await RequestDataAsync(
            http,
            HttpMethod.Get,
            "/contents/" + Uri.EscapeDataString(id),
            token,
            BuildContentQuery(password),
            cancellationToken).ConfigureAwait(false);
        var probe = ParseFolder(data);
        var needsPassword = probe.Items.Count == 0 && !data.TryGetProperty("children", out _);
        return new ListedFolder(probe, probe.Items, token, needsPassword);
    }

    private static string BuildContentQuery(string? password)
    {
        var query =
            "contentFilter=&page=1&pageSize=1000&sortField=name&sortDirection=1";
        if (!string.IsNullOrEmpty(password))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)))
                .ToLowerInvariant();
            query += "&password=" + hash;
        }

        return query;
    }

    private async Task<string> EnsureTokenAsync(CancellationToken cancellationToken)
    {
        var path = TokenPath();
        try
        {
            if (File.Exists(path))
            {
                var saved = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
                if (saved.Length > 8)
                    return saved;
            }
        }
        catch
        {
        }

        using var http = NewClient();
        var data = await RequestDataAsync(http, HttpMethod.Post, "/accounts", token: null, query: null, cancellationToken)
            .ConfigureAwait(false);
        var token = ReadString(data, "token") ?? "";
        if (token.Length == 0)
            throw new InvalidOperationException("Could not read that link.");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, token, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        return token;
    }

    private static void ClearToken()
    {
        try
        {
            var path = TokenPath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string TokenPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WasdFetchIt",
            "gofile-guest.txt");

    internal static bool LooksLikeSsl(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
                return true;
            var text = current.Message;
            if (text.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || text.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                || text.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static SocketsHttpHandler CreateHandler(bool cookies)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = GalleryDlService.DirectParallel,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = false,
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }
        };
        if (cookies)
        {
            handler.UseCookies = true;
            handler.CookieContainer = new CookieContainer();
        }

        return handler;
    }

    private static HttpClient CreateClient(SocketsHttpHandler handler, TimeSpan timeout)
    {
        var http = new HttpClient(handler)
        {
            Timeout = timeout,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://gofile.io");
        http.DefaultRequestHeaders.Referrer = new Uri("https://gofile.io/");
        return http;
    }

    private static HttpClient NewClient() => CreateClient(CreateHandler(cookies: false), TimeSpan.FromMinutes(1));

    private static async Task<JsonElement> RequestDataAsync(
        HttpClient http,
        HttpMethod method,
        string endpoint,
        string? token,
        string? query,
        CancellationToken cancellationToken)
    {
        var url = "https://api.gofile.io" + endpoint;
        if (!string.IsNullOrEmpty(query))
            url += "?" + query;

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            request.Headers.TryAddWithoutValidation("X-BL", Lang);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.TryAddWithoutValidation(
                    "X-Website-Token",
                    WebsiteToken(UserAgent, token, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            }

            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (attempt < 2 && LooksLikeSsl(ex))
            {
            }
        }

        if (response is null)
            throw new InvalidOperationException(SslMessage);

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 429)
                throw new InvalidOperationException("GoFile is busy. Try again in a minute.");

            if (TryReadData(text, out var data, out var error))
                return data;
            if (error is not null)
                throw new InvalidOperationException(error);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
            throw new InvalidOperationException("Could not read that link.");
        }
    }

    internal static bool TryReadData(string text, out JsonElement data, out string? error)
    {
        data = default;
        error = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var status = ReadString(root, "status");
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "ok-no-cache", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("data", out var payload))
                {
                    error = "Could not read that link.";
                    return false;
                }

                data = payload.Clone();
                return true;
            }

            if (status is not null)
                error = MessageForStatus(status);
            return false;
        }
        catch (JsonException)
        {
            error = "GoFile is busy. Try again in a minute.";
            return false;
        }
    }

    internal static string? HttpsLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var text = url.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal))
            text = "https:" + text;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(text, UriKind.Absolute, out var http)
            && (http.Host.EndsWith("gofile.io", StringComparison.OrdinalIgnoreCase)))
            text = "https://" + http.Host + http.PathAndQuery;
        return MediaRouter.TryParseHttpUrl(text, out _) ? text : null;
    }

    internal static string MessageForStatus(string status)
    {
        if (status.Contains("password", StringComparison.OrdinalIgnoreCase))
            return "That folder needs a password.";
        if (status.Contains("notFound", StringComparison.OrdinalIgnoreCase)
            || status.Contains("notExist", StringComparison.OrdinalIgnoreCase))
            return "That GoFile folder is gone.";
        if (status.Contains("notPremium", StringComparison.OrdinalIgnoreCase))
            return "GoFile blocked that folder.";
        return "Could not read that link.";
    }

    private static void TryAddFile(JsonElement content, List<MediaItem> items)
    {
        var type = ReadString(content, "type");
        if (!string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            return;
        var link = HttpsLink(ReadString(content, "link") ?? ReadString(content, "directLink"));
        if (link is null)
            return;

        var name = ReadString(content, "name") ?? "file";
        var mime = ReadString(content, "mimetype") ?? "";
        var isVideo = mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                      || LooksVideoName(name);
        var isImage = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                      || LooksImageName(name);
        items.Add(new MediaItem
        {
            Kind = isVideo ? MediaKind.Video : MediaKind.Image,
            Title = name,
            ThumbnailUrl = isImage ? link : ReadString(content, "thumbnail"),
            DownloadUrl = link
        });
    }

    private static bool LooksVideoName(string name)
    {
        var ext = Path.GetExtension(name).Trim('.').ToLowerInvariant();
        return ext is "mp4" or "mkv" or "webm" or "mov" or "m4v" or "avi";
    }

    private static bool LooksImageName(string name)
    {
        var ext = Path.GetExtension(name).Trim('.').ToLowerInvariant();
        return ext is "jpg" or "jpeg" or "png" or "webp" or "gif" or "bmp" or "avif";
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
