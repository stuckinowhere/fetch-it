namespace FetchIt.Models;

public enum VideoQuality
{
    Best,
    P1080,
    P720,
    Audio
}

public static class VideoQualityText
{
    public static string Label(VideoQuality quality) => quality switch
    {
        VideoQuality.Best => "Best",
        VideoQuality.P1080 => "1080p",
        VideoQuality.P720 => "720p",
        VideoQuality.Audio => "Audio",
        _ => "Best"
    };

    public static VideoQuality Next(VideoQuality quality) => quality switch
    {
        VideoQuality.Best => VideoQuality.P1080,
        VideoQuality.P1080 => VideoQuality.P720,
        VideoQuality.P720 => VideoQuality.Audio,
        _ => VideoQuality.Best
    };
}
