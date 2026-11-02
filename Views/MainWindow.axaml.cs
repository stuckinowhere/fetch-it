using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Tunnel so the shortcut works while the URL box has focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
            Title = L.SaveFolderTip,
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> ReadClipboardAsync()
    {
        var clipboard = Clipboard;
        return clipboard is null ? null : await clipboard.TryGetTextAsync();
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
            L.Clipboard,
            $"{L.PasteThisLink}\n\n{MainViewModel.ShortLink(link)}",
            L.Paste);
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

    private static string? DroppedLink(DragEventArgs e)
    {
        try
        {
            var text = e.DataTransfer.TryGetText();
            if (MediaRouter.TryParseHttpUrl(text, out _))
                return text!.Trim();
        }
        catch
        {
        }

        return null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedLink(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var link = DroppedLink(e);
        if (link is null || DataContext is not MainViewModel vm)
            return;
        e.Handled = true;
        await vm.AcceptDroppedLinkAsync(link);
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

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        if (DataContext is not MainViewModel vm || !vm.CanDownload)
            return;
        e.Handled = true;
        await vm.DownloadCommand.ExecuteAsync(null);
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
