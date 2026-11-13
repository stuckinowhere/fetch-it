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
        RestoreSpot();
        // For test runs, which must not pull focus from whatever the user is doing.
        ShowActivated = Environment.GetEnvironmentVariable("FETCHIT_OPEN_QUIETLY") is null;
        Activated += OnActivated;
        Opened += OnOpened;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Tunnel so the shortcut works while the URL box has focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        App.SingleInstance?.Watch(() => Dispatcher.UIThread.Post(ShowExisting));
    }

    private void RestoreSpot()
    {
        if (WindowStore.Load() is not { } spot
            || !WindowStore.IsReachable(spot, Screens.All.Select(screen => screen.WorkingArea)))
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(spot.X, spot.Y);
        Width = Math.Max(MinWidth, spot.Width);
        Height = Math.Max(MinHeight, spot.Height);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (WindowState == WindowState.Normal)
            WindowStore.Save(new WindowSpot(Position.X, Position.Y, ClientSize.Width, ClientSize.Height));
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

    public Task<bool> SignInInstagramAsync(CancellationToken cancellationToken)
        => InstagramSignInFlow.RunAsync(this, cancellationToken);

    public Task ShowAccountAsync() => new AccountWindow().ShowDialog(this);

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

    public async Task<string?> AskQualityAsync(string title, IReadOnlyList<MediaQuality> qualities)
    {
        var window = new QualityWindow(title, qualities);
        return await window.ShowDialog<string?>(this);
    }

    public async Task<bool?> AskIncludeVideosAsync(int images, int videos)
    {
        var window = new MediaChoiceWindow(images, videos);
        return await window.ShowDialog<bool?>(this);
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

    private void OnTileShown(object? sender, VisualTreeAttachmentEventArgs e)
        => ((sender as Control)?.DataContext as PreviewCard)?.Show();

    private void OnTileHidden(object? sender, VisualTreeAttachmentEventArgs e)
        => ((sender as Control)?.DataContext as PreviewCard)?.Hide();

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
        => FitPreview(e.NewSize.Width, e.NewSize.Height);

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
