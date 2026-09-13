using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FetchIt.Services;

namespace FetchIt.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateCheckResult _result;
    private bool _busy;
    private bool _closeOnly;

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
                Body.Text = UpdateInstaller.IsSetupDownload(_result.DownloadUrl)
                    ? $"fetch it {_result.Tag} is ready. Install now? The app will close, then open again."
                    : $"fetch it {_result.Tag} is ready. Open the release page to get the setup.";
                PrimaryLabel.Text = UpdateInstaller.IsSetupDownload(_result.DownloadUrl) ? "Install" : "Open";
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

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_closeOnly || _result.Status != UpdateCheckStatus.Available)
        {
            Close();
            return;
        }

        var url = _result.DownloadUrl ?? _result.ReleaseUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            Close();
            return;
        }

        if (!UpdateInstaller.IsSetupDownload(url))
        {
            GitHubUpdateClient.TryOpenUrl(url);
            Close();
            return;
        }

        _busy = true;
        PrimaryButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        DownloadProgress.IsVisible = true;
        DownloadProgress.IsIndeterminate = true;
        PrimaryLabel.Text = "Installing…";
        Body.Text = "Downloading the setup…";

        try
        {
            using var installer = new UpdateInstaller();
            var progress = new Progress<double>(value =>
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = value;
            });
            await installer.InstallAsync(url, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _busy = false;
            _closeOnly = true;
            PrimaryButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            DownloadProgress.IsVisible = false;
            Eyebrow.Text = "UPDATE FAILED";
            if (this.TryFindResource("DangerBrush", out var brush) && brush is IBrush colored)
                Eyebrow.Foreground = colored;
            Body.Text = Short(ex.Message);
            PrimaryLabel.Text = "OK";
            LaterButton.IsVisible = false;
        }
    }

    private void OnLaterClick(object? sender, RoutedEventArgs e)
    {
        if (!_busy)
            Close();
    }

    private static string Short(string message)
    {
        var line = message.Replace('\n', ' ').Trim();
        return line.Length > 120 ? line[..120].Trim() : line;
    }
}
