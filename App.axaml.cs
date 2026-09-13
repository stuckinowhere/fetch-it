using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FetchIt.Services;
using FetchIt.ViewModels;
using FetchIt.Views;

namespace FetchIt;

public partial class App : Application
{
    internal static SingleInstance? SingleInstance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ThemeStore.ApplySaved(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel() };

        base.OnFrameworkInitializationCompleted();
    }
}
