using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FetchIt.Models;
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
        Opened += OnOpened;
        SizeChanged += OnWindowSizeChanged;
        App.SingleInstance?.Watch(() => Dispatcher.UIThread.Post(ShowExisting));
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

    public async Task ShowUpdateAsync(UpdateCheckResult result)
    {
        var window = new UpdateWindow(result);
        await window.ShowDialog(this);
    }

    public async Task ShowAlertAsync(string heading, string message, bool isError)
    {
        var window = new AlertWindow(heading, message, isError);
        await window.ShowDialog(this);
    }

    public async Task<DuplicateChoice> AskIfAlreadySavedAsync(string folderLabel, IReadOnlyList<string> names)
    {
        var window = new DuplicateWindow(folderLabel, names);
        var choice = await window.ShowDialog<DuplicateChoice>(this);
        return choice;
    }

    public async Task<string?> AskQualityAsync(string title, IReadOnlyList<MediaQuality> qualities)
    {
        var window = new QualityWindow(title, qualities);
        return await window.ShowDialog<string?>(this);
    }

    public async Task<bool> AskPasteLinkAsync(string link)
    {
        var window = new ConfirmWindow(
            "Clipboard",
            $"Paste this link?\n\n{MainViewModel.ShortLink(link)}",
            "Paste");
        return await window.ShowDialog<bool>(this);
    }

    private async void OnActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.PasteIfEmptyAsync();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (DataContext is not MainViewModel vm)
            return;
        await Task.Delay(1200);
        if (IsVisible)
            await vm.CheckUpdatesOnLaunchAsync();
    }

    private void ShowExisting()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
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
