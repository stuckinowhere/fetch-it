using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FetchIt.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow() : this("Confirm", "Continue?", "Yes")
    {
    }

    public ConfirmWindow(string heading, string message, string yesLabel)
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
        Eyebrow.Text = heading.ToUpperInvariant();
        Body.Text = message;
        YesLabel.Text = yesLabel;
    }

    private void OnYesClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNoClick(object? sender, RoutedEventArgs e) => Close(false);
}
