using Avalonia;
using FetchIt.Services;

namespace FetchIt;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
