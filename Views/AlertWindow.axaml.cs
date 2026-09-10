using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace FetchIt.Views;

public partial class AlertWindow : Window
{
    public AlertWindow() : this("Notice", "", false)
    {
    }

    public AlertWindow(string heading, string message, bool isError)
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
        Eyebrow.Text = heading.ToUpperInvariant();
        Body.Text = message;
        var key = isError ? "DangerBrush" : "SuccessBrush";
        if (this.TryFindResource(key, out var brush) && brush is IBrush colored)
            Eyebrow.Foreground = colored;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
