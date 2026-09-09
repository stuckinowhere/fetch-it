namespace FetchIt.Models;

public enum EngineKind
{
    YtDlp,
    GalleryDl
}

public sealed class MediaProbe
{
    public string Title { get; init; } = "";
    public string Site { get; init; } = "";
    public TimeSpan? Duration { get; init; }
    public int VideoCount { get; init; }
    public int ImageCount { get; init; }
    public bool NeedsLogin { get; init; }
    public EngineKind Engine { get; init; }
    public bool HasVideo => VideoCount > 0;
    public bool HasImages => ImageCount > 0;
    public int FileCount => VideoCount + ImageCount;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Site))
                parts.Add(Site);
            if (Duration is { } duration && duration > TimeSpan.Zero)
                parts.Add(FormatDuration(duration));
            if (VideoCount > 0)
                parts.Add(VideoCount == 1 ? "1 video" : $"{VideoCount} videos");
            if (ImageCount > 0)
                parts.Add(ImageCount == 1 ? "1 image" : $"{ImageCount} images");
            return string.Join("  ·  ", parts);
        }
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        return $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
    }
}

public sealed class FetchProgress
{
    public double Percent { get; init; }
    public string Status { get; init; } = "";
}
