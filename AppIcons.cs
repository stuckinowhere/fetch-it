using Avalonia.Controls;
using Avalonia.Platform;

namespace FetchIt;

internal static class AppIcons
{
    public static WindowIcon? Load()
    {
        try
        {
            var ico = NativeWindowIcon.FindSidecarIco();
            if (ico is not null)
            {
                using var file = File.OpenRead(ico);
                return new WindowIcon(file);
            }

            using var stream = AssetLoader.Open(new Uri("avares://FetchIt/Assets/fetchit.png"));
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    public static void ApplyToWindow(Window window)
    {
        var icon = Load();
        if (icon is not null)
            window.Icon = icon;
    }
}
