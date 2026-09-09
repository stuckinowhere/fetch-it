using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FetchIt.Controls;
using FetchIt.ViewModels;

namespace FetchIt.Views;

public partial class MainWindow : Window, IUiHost
{
    public MainWindow()
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
        SizeChanged += (_, _) => ClipToCutCorners();
        Opened += (_, _) => ClipToCutCorners();
        Activated += OnActivated;
        PropertyChanged += OnWindowPropertyChanged;
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
            Title = "FOLDER",
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> ReadClipboardAsync()
    {
        var clipboard = Clipboard;
        return clipboard is null ? null : await clipboard.GetTextAsync();
    }

    private async void OnActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.PasteIfEmptyAsync();
    }

    private void ClipToCutCorners()
    {
        var cut = Shell?.CutSize ?? 22;
        Clip = CutCornerFrame.CreateGeometry(Bounds.Width, Bounds.Height, cut);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnChromePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);
    }
}
