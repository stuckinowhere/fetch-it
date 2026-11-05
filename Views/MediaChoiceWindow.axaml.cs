using Avalonia.Controls;
using Avalonia.Interactivity;
using FetchIt.Services;

namespace FetchIt.Views;

/// <summary>Closes with true for everything, false for photos only, null for cancel.</summary>
public partial class MediaChoiceWindow : Window
{
    public MediaChoiceWindow() : this(1, 1)
    {
    }

    public MediaChoiceWindow(int images, int videos)
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
        Eyebrow.Text = L.Download.ToUpperInvariant();
        Body.Text = L.Format("{0:N0} photos and {1:N0} videos. What do you want to save?", images, videos);
    }

    private void OnEverythingClick(object? sender, RoutedEventArgs e) => Close((bool?)true);

    private void OnPhotosOnlyClick(object? sender, RoutedEventArgs e) => Close((bool?)false);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close((bool?)null);
}
