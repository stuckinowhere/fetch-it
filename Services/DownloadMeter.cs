using System.Diagnostics;
using System.Globalization;
using FetchIt.Models;

namespace FetchIt.Services;

internal sealed class DownloadMeter
{
    private long _origin;
    private long _startTimestamp;

    public void Reset(long alreadyHave = 0)
    {
        _origin = alreadyHave;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    public FetchProgress Snapshot(long written, long? total)
    {
        var speed = BytesPerSecond(written);
        var speedText = FormatSpeed(speed);
        if (total is > 0)
        {
            var percent = Math.Clamp(100.0 * written / total.Value, 0, 99.5);
            return new FetchProgress
            {
                Percent = percent,
                HasPercent = true,
                Status = $"{FormatBytes(written)} / {FormatBytes(total.Value)}  ·  {speedText}"
            };
        }

        return new FetchProgress
        {
            Percent = 0,
            HasPercent = false,
            Status = $"Saving… {FormatBytes(written)}  ·  {speedText}"
        };
    }

    public double BytesPerSecond(long written)
    {
        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        if (elapsed < 0.05)
            return 0;
        return Math.Max(0, (written - _origin) / elapsed);
    }

    internal static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return "…";
        var mb = bytesPerSecond / (1024d * 1024d);
        if (mb >= 0.1)
            return string.Create(CultureInfo.InvariantCulture, $"{mb:0.0} MB/s");
        var kb = bytesPerSecond / 1024d;
        return string.Create(CultureInfo.InvariantCulture, $"{kb:0} KB/s");
    }

    internal static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
