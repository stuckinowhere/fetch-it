using System.Text.RegularExpressions;
using FetchIt.Models;

namespace FetchIt.Services;

/// <summary>
/// A private folder for bundled tools to download into; FetchIt then moves the
/// finished files to the save folder. Windows Controlled Folder Access blocks
/// unknown programs like gallery-dl.exe from protected folders, and a tool sees
/// that only as "file not found". Unfinished files stay behind and are deleted.
/// </summary>
internal sealed class ToolStaging : IDisposable
{
    // Partial downloads, plus yt-dlp's unmerged streams and merge output.
    private static readonly Regex Unfinished = new(
        @"(\.part|\.ytdl|\.temp|\.tmp|\.part-Frag\d+|\.f\d+\.(mp4|webm|m4a|mkv)|\.temp\.[^.]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly TimeSpan LeftoverAge = TimeSpan.FromDays(1);

    private ToolStaging(string folder) => Folder = folder;

    public string Folder { get; }

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "staging");

    public static ToolStaging Create(string? root = null)
    {
        var parent = root ?? DefaultRoot;
        RemoveLeftovers(parent);
        var folder = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return new ToolStaging(folder);
    }

    /// <summary>Move every finished file to <paramref name="dest"/>, keeping sub folders.</summary>
    public int MoveTo(string dest, DuplicateChoice duplicate)
    {
        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories).ToList())
        {
            if (Unfinished.IsMatch(file))
                continue;

            var relative = Path.GetRelativePath(Folder, file);
            if (duplicate == DuplicateChoice.Skip && File.Exists(Path.Combine(dest, relative)))
                continue;

            try
            {
                var target = duplicate == DuplicateChoice.Overwrite
                    ? Path.Combine(dest, relative)
                    : SaveClash.UniquePath(dest, relative);
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                File.Move(file, target, overwrite: duplicate == DuplicateChoice.Overwrite);
                moved++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
            {
                AppLog.Warn($"Could not move {relative} to the save folder: {ex.Message}");
                throw new InvalidOperationException("Windows blocked that folder. Pick another save folder.", ex);
            }
        }

        return moved;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A tool may still hold a file; the next download clears it.
            AppLog.Warn($"Could not remove staging folder: {ex.Message}");
        }
    }

    private static void RemoveLeftovers(string parent)
    {
        if (!Directory.Exists(parent))
            return;
        foreach (var old in Directory.EnumerateDirectories(parent))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(old) > LeftoverAge)
                    Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Could not remove old staging folder: {ex.Message}");
            }
        }
    }
}
