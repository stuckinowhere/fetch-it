using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;

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
        // Bound, not read once: here the window has no theme yet, so a lookup finds nothing.
        Eyebrow.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(isError ? "DangerBrush" : "SuccessBrush"));
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
