using System.Net;
using System.Security.Cryptography;
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

    public async Task<MediaProbe> ProbeAsync(
        string url,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryParseFolder(url, out var id, out var password))
            throw new InvalidOperationException("Not a GoFile link.");

        progress?.Report(new FetchProgress { Status = "Reading folder…" });
        var folder = await ListFolderAsync(id, password, cancellationToken).ConfigureAwait(false);
        if (folder.Items.Count == 0)
            throw new InvalidOperationException(
                folder.NeedsPassword ? "That folder needs a password." : "Could not read that link.");
        return folder.Probe;
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

        progress.Report(new FetchProgress { Status = "Reading folder…" });
        var listed = await ListFolderAsync(id, password, cancellationToken).ConfigureAwait(false);
        var fresh = listed.Items.Count > 0 ? listed.Probe : probe;
        var jobs = GalleryDlService.PlanDirectFiles(dest, fresh, duplicate);
        if (jobs.Count == 0)
            throw new InvalidOperationException("Could not save those files.");

        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = GalleryDlService.DirectParallel,
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        if (!string.IsNullOrEmpty(listed.Token))
            handler.CookieContainer.Add(new Cookie("accountToken", listed.Token, "/", ".gofile.io"));

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://gofile.io");
        http.DefaultRequestHeaders.Referrer = new Uri("https://gofile.io/");
        if (!string.IsNullOrEmpty(listed.Token))
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + listed.Token);

        await GalleryDlService.DownloadDirectAsync(jobs, progress, cancellationToken, http).ConfigureAwait(false);
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

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://gofile.io");
        http.DefaultRequestHeaders.Referrer = new Uri("https://gofile.io/");
        return http;
    }

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

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-BL", Lang);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation(
                "X-Website-Token",
                WebsiteToken(UserAgent, token, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("GoFile is busy. Try again in a minute.");

        if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var status = ReadString(root, "status");
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "ok-no-cache", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("data", out var data))
                    throw new InvalidOperationException("Could not read that link.");
                return data.Clone();
            }

            if (status is not null)
                throw new InvalidOperationException(MessageForStatus(status));
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        throw new InvalidOperationException("Could not read that link.");
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
        var link = ReadString(content, "link");
        if (!MediaRouter.TryParseHttpUrl(link, out _))
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
