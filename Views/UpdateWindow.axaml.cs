using Avalonia.Controls;
using Avalonia.Interactivity;
using FetchIt.Services;

namespace FetchIt.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateCheckResult _result;

    public UpdateWindow() : this(new UpdateCheckResult(
        UpdateCheckStatus.Current,
        new Version(1, 0, 0, 0),
        new Version(1, 0, 0, 0),
        "v1.0.0",
        null,
        null,
        null))
    {
    }

    public UpdateWindow(UpdateCheckResult result)
    {
        _result = result;
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
        ApplyResult();
    }

    private void ApplyResult()
    {
        switch (_result.Status)
        {
            case UpdateCheckStatus.Available:
                Eyebrow.Text = "UPDATE AVAILABLE";
                Body.Text =
                    $"fetch it {_result.Tag} is ready. Download the setup and run it — the installer will close this copy first.";
                PrimaryLabel.Text = "Download";
                LaterButton.IsVisible = true;
                break;
            case UpdateCheckStatus.Failed:
                Eyebrow.Text = "UPDATE CHECK";
                Body.Text = _result.Error ?? "Could not check for updates. Try again when you are online.";
                PrimaryLabel.Text = "OK";
                LaterButton.IsVisible = false;
                break;
            default:
                Eyebrow.Text = "UP TO DATE";
                Body.Text = $"You are on {_result.Current.ToString(3)}.";
                PrimaryLabel.Text = "OK";
                LaterButton.IsVisible = false;
                break;
        }
    }

    private void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (_result.Status == UpdateCheckStatus.Available)
        {
            var url = _result.DownloadUrl ?? _result.ReleaseUrl;
            if (!string.IsNullOrWhiteSpace(url))
                GitHubUpdateClient.OpenUrl(url);
        }

        Close();
    }

    private void OnLaterClick(object? sender, RoutedEventArgs e) => Close();
}
