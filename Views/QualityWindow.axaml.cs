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
        Body.Text = Services.L.Format("Pick a quality for “{0}”.", title);

        foreach (var quality in qualities)
        {
            var text = new TextBlock
            {
                Text = quality.Label,
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
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

    private void OnQualityClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            Close(url);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
