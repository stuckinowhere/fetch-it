using System.Globalization;
using FetchIt.Models;

namespace FetchIt.Services;

/// <summary>Moves files with no people into a subfolder, where they can be reviewed. Nothing is deleted.</summary>
public static class PeopleSorter
{
    public sealed record Outcome(int Moved, int Unread);

    private static readonly double[] FrameFractions = [0.1, 0.3, 0.5, 0.7, 0.9];

    /// <summary>Checks the files just downloaded. Other files in the folder are not touched.</summary>
    public static async Task<Outcome> SeparateAsync(
        string folder,
        IReadOnlyList<string> names,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var tools = await ToolBootstrapper.EnsureAsync(null, cancellationToken).ConfigureAwait(false);
        using var detector = await PersonDetector.LoadAsync(cancellationToken).ConfigureAwait(false);
        // Controlled Folder Access can block ffmpeg in the save folder, so video frames go here.
        using var staging = ToolStaging.Create();
        var frame = Path.Combine(staging.Folder, "frame.jpg");

        return await MoveAsync(
            folder,
            names,
            (path, ct) => CheckAsync(detector, tools, frame, path, ct),
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Outcome> MoveAsync(
        string folder,
        IReadOnlyList<string> names,
        Func<string, CancellationToken, Task<PeopleCheck>> check,
        IProgress<FetchProgress> progress,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(folder, L.WithoutPeople);
        var moved = 0;
        var unread = 0;
        for (var i = 0; i < names.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new FetchProgress
            {
                HasPercent = true,
                Percent = 100.0 * i / names.Count,
                Status = L.Format("Checking {0} of {1}…", i + 1, names.Count)
            });

            var path = Path.Combine(folder, names[i]);
            if (!File.Exists(path))
                continue;

            switch (await check(path, cancellationToken).ConfigureAwait(false))
            {
                case PeopleCheck.NoPeople:
                    MoveInto(target, path);
                    moved++;
                    break;
                case PeopleCheck.Unknown:
                    unread++;
                    break;
            }
        }

        AppLog.Info($"people check: {names.Count} files, {moved} moved, {unread} unread");
        return new Outcome(moved, unread);
    }

    private static void MoveInto(string target, string path)
    {
        try
        {
            Directory.CreateDirectory(target);
            File.Move(path, Path.Combine(target, Path.GetFileName(path)), overwrite: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AppLog.Warn($"Could not move {Path.GetFileName(path)}: {ex.Message}");
            throw new InvalidOperationException("Windows blocked that folder. Pick another save folder.", ex);
        }
    }

    private static Task<PeopleCheck> CheckAsync(
        PersonDetector detector,
        ToolPaths tools,
        string frame,
        string path,
        CancellationToken cancellationToken)
        => SaveClash.IsVideoName(path)
            ? CheckVideoAsync(detector, tools, frame, path, cancellationToken)
            : Task.Run(() => detector.CheckImage(path), cancellationToken);

    // A person in any sampled frame keeps the video.
    private static async Task<PeopleCheck> CheckVideoAsync(
        PersonDetector detector,
        ToolPaths tools,
        string frame,
        string path,
        CancellationToken cancellationToken)
    {
        var ffmpeg = Path.Combine(tools.FfmpegDir, "ffmpeg.exe");
        var seconds = await ReadDurationAsync(tools, path, cancellationToken).ConfigureAwait(false);

        var result = PeopleCheck.Unknown;
        foreach (var time in FrameTimes(seconds))
        {
            await ProcessRunner.RunAsync(
                ffmpeg,
                [
                    "-nostdin", "-loglevel", "error",
                    "-ss", time.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", path,
                    "-frames:v", "1",
                    "-vf", "scale=640:-2",
                    "-y", frame
                ],
                null,
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(frame))
                continue;

            var check = await Task.Run(() => detector.CheckImage(frame), cancellationToken).ConfigureAwait(false);
            File.Delete(frame);
            if (check == PeopleCheck.HasPeople)
                return PeopleCheck.HasPeople;
            if (check == PeopleCheck.NoPeople)
                result = PeopleCheck.NoPeople;
        }

        return result;
    }

    private static async Task<double> ReadDurationAsync(ToolPaths tools, string path, CancellationToken cancellationToken)
    {
        try
        {
            var text = await ProcessRunner.RunTextAsync(
                Path.Combine(tools.FfmpegDir, "ffprobe.exe"),
                ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path],
                cancellationToken).ConfigureAwait(false);
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    internal static IReadOnlyList<double> FrameTimes(double seconds)
        => seconds > 0 ? FrameFractions.Select(fraction => fraction * seconds).ToList() : [0];
}
