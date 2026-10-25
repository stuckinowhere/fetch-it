using Avalonia;
using FetchIt.Services;

namespace FetchIt;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CleanOldFiles();
        if (!SingleInstance.TryOwn(out var instance) || instance is null)
            return;

        using (instance)
        {
            NativeWindowIcon.SetProcessAppId();
            BuildAvaloniaApp()
                .AfterSetup(_ => App.SingleInstance = instance)
                .StartWithClassicDesktopLifetime(args);
        }
    }

    private static void CleanOldFiles()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            foreach (var file in Directory.EnumerateFiles(dir, "*.old", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
