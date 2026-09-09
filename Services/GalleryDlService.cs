using FetchIt.Models;

namespace FetchIt.Services;

public sealed class GalleryDlService
{
    public async Task<MediaProbe> ProbeAsync(string url, bool useCookies, CancellationToken cancellationToken)
    {
        var tools = await ToolBootstrapper.EnsureAsync(null, cancellationToken).ConfigureAwait(false);
        var args = new List<string> { "--dump-json", "--no-download" };
        AddCookies(args, useCookies);
        args.Add(url);

        var text = await ProcessRunner.RunTextAsync(tools.GalleryDl, args, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text) || LooksLikeFailure(text))
            throw new InvalidOperationException(ShortError(text));
        return GalleryDlParser.Parse(text, needsLogin: useCookies || MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.NeedsLoginRow(uri));
    }

    public async Task DownloadAsync(
        string url,
        string folder,
        string title,
        int fileCount,
        IProgress<FetchProgress> progress,
        bool useCookies,
        CancellationToken cancellationToken)
    {
        var tools = await ToolBootstrapper.EnsureAsync(new Progress<string>(_ =>
        {
            progress.Report(new FetchProgress { Status = "tools" });
        }), cancellationToken).ConfigureAwait(false);

        var dest = fileCount > 1
            ? Path.Combine(folder, MediaRouter.SanitizeFolderName(title))
            : folder;
        Directory.CreateDirectory(dest);

        var done = 0;
        var args = new List<string>
        {
            "-D", dest,
            "--no-mtime"
        };
        AddCookies(args, useCookies);
        args.Add(url);

        var code = await ProcessRunner.RunAsync(tools.GalleryDl, args, line =>
        {
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                return;
            if (LooksLikeSavedFile(line))
            {
                done++;
                var total = Math.Max(fileCount, done);
                progress.Report(new FetchProgress
                {
                    Percent = total == 0 ? 0 : 100.0 * done / total,
                    Status = $"{done} / {total}"
                });
            }
        }, cancellationToken).ConfigureAwait(false);

        if (code != 0 && done == 0)
            throw new InvalidOperationException("Could not fetch that post.");
    }

    internal static void AddCookies(List<string> args, bool useCookies)
    {
        if (!useCookies)
            return;
        args.Add("--cookies-from-browser");
        args.Add("chrome");
    }

    internal static bool LooksLikeSavedFile(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;
        return trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.StartsWith('#');
    }

    private static bool LooksLikeFailure(string text)
    {
        var trimmed = text.TrimStart();
        return !trimmed.Contains('{') && !trimmed.Contains('[');
    }

    private static string ShortError(string text)
    {
        if (text.Contains("login", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return "Needs login.";
        return "Could not read that link.";
    }
}
