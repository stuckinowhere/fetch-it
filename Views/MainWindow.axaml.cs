using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FetchIt.Services;
using FetchIt.ViewModels;

namespace FetchIt.Views;

public partial class MainWindow : Window, IUiHost
{
    public MainWindow()
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
        Activated += OnActivated;
        SizeChanged += OnWindowSizeChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
            vm.Ui = this;
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Save to",
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> ReadClipboardAsync()
    {
        var clipboard = Clipboard;
        return clipboard is null ? null : await clipboard.GetTextAsync();
    }

    public async Task<bool> SignInInstagramAsync(CancellationToken cancellationToken)
    {
        var window = new InstagramLoginWindow();
        await using var close = cancellationToken.Register(() =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (window.IsVisible)
                    window.Close(false);
            });
        });
        var result = await window.ShowDialog<bool>(this);
        return result && SessionCookies.HasUsableFile();
    }

    private async void OnActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.PasteIfEmptyAsync();
    }

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
        => FitPreview(e.NewSize.Width, e.NewSize.Height);

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
        => FitPreviewFromWindow();

    private void FitPreviewFromWindow()
    {
        if (DataContext is not MainViewModel vm)
            return;
        var width = Math.Max(0, Bounds.Width - 48);
        var height = Math.Max(0, Bounds.Height - 220);
        vm.FitPreview(width, height);
    }

    private void FitPreview(double width, double height)
    {
        if (DataContext is MainViewModel vm)
            vm.FitPreview(width, height);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F11)
            return;

        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;
        e.Handled = true;
    }
}
