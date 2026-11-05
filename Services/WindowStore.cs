using System.Text.Json;
using Avalonia;

namespace FetchIt.Services;

public sealed record WindowSpot(int X, int Y, double Width, double Height);

/// <summary>Remembers where the window was, so it reopens on the same screen.</summary>
public static class WindowStore
{
    // Enough of the title bar to grab, so the window can never open out of reach.
    private const int MinVisible = 100;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WasdFetchIt",
        "window.json");

    public static WindowSpot? Load(string? path = null)
    {
        try
        {
            return JsonSerializer.Deserialize<WindowSpot>(File.ReadAllText(path ?? FilePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(WindowSpot spot, string? path = null)
    {
        var file = path ?? FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(spot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not save the window position: {ex.Message}");
        }
    }

    /// <summary>False when the screen it was on is gone, so the window opens centred instead.</summary>
    internal static bool IsReachable(WindowSpot spot, IEnumerable<PixelRect> screens)
        => screens.Any(screen =>
            screen.Intersects(new PixelRect(spot.X, spot.Y, MinVisible, MinVisible))
            && spot.X + MinVisible <= screen.Right
            && spot.Y >= screen.Y
            && spot.Y + MinVisible <= screen.Bottom);
}
