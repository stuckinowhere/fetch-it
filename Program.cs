using Avalonia;
using FetchIt.Services;

namespace FetchIt;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        CleanOldFiles();
        if (!SingleInstance.TryOwn(out var instance) || instance is null)
            return;

        AppLog.Info($"fetch it {AppVersion.Current.ToString(3)} starting ({L.Language})");
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
