using Avalonia;

namespace FetchIt;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        NativeWindowIcon.SetProcessAppId();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
