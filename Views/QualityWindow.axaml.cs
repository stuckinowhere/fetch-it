using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FetchIt.Models;

namespace FetchIt.Views;

public partial class QualityWindow : Window
{
    public QualityWindow() : this("Video",
    [
        new MediaQuality { Label = "HD", Url = "https://example.com/hd.mp4" }
    ])
    {
    }

    public QualityWindow(string title, IReadOnlyList<MediaQuality> qualities)
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
        Body.Text = $"Pick a quality for “{title}”.";

        var ink = ResolveBrush("InkBrush") ?? Brushes.White;

        foreach (var quality in qualities)
        {
            var text = new TextBlock
            {
                Text = quality.Label,
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ink
            };

            var button = new Button
            {
                Content = text,
                Height = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Tag = quality.Url
            };
            button.Classes.Add("Flat");
            button.Click += OnQualityClick;
            Choices.Children.Add(button);
        }
    }

    private IBrush? ResolveBrush(string key)
    {
        if (this.TryFindResource(key, out var value) && value is IBrush brush)
            return brush;
        return null;
    }

    private void OnQualityClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            Close(url);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
