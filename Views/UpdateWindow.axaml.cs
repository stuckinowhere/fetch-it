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
        var canInstall = UpdateInstaller.IsSupportedDownload(_result.DownloadUrl)
            || UpdateInstaller.IsZipDownload(_result.ZipUrl);
        switch (_result.Status)
        {
            case UpdateCheckStatus.Available:
                Eyebrow.Text = L.T("UPDATE AVAILABLE");
                Body.Text = canInstall
                    ? L.Format("fetch it {0} is ready. Install now? The app will close, then open again.", _result.Tag ?? "")
                    : L.Format("fetch it {0} is ready. Open the release page to get the setup.", _result.Tag ?? "");
                PrimaryLabel.Text = canInstall ? L.Install : L.Open;
                LaterButton.IsVisible = true;
                break;
            case UpdateCheckStatus.Failed:
                Eyebrow.Text = L.T("UPDATE CHECK");
                Body.Text = L.Translate(_result.Error ?? "Could not check for updates. Try again when you are online.");
                PrimaryLabel.Text = L.Ok;
                LaterButton.IsVisible = false;
                break;
            default:
                Eyebrow.Text = L.T("UP TO DATE");
                Body.Text = L.Format("You are on {0}.", _result.Current.ToString(3));
                PrimaryLabel.Text = L.Ok;
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

        var canInstall = UpdateInstaller.IsSupportedDownload(url)
            || UpdateInstaller.IsZipDownload(_result.ZipUrl);
        if (!canInstall)
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
        PrimaryLabel.Text = L.T("Installing…");
        Body.Text = L.T("Downloading update…");

        try
        {
            using var installer = new UpdateInstaller();
            var progress = new Progress<double>(value =>
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = value;
            });
            await installer.InstallAsync(_result, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Error("Update install failed", ex);
            _busy = false;
            _closeOnly = true;
            PrimaryButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            DownloadProgress.IsVisible = false;
            Eyebrow.Text = L.T("UPDATE FAILED");
            if (this.TryFindResource("DangerBrush", out var brush) && brush is IBrush colored)
                Eyebrow.Foreground = colored;
            Body.Text = Short(L.Translate(ex.Message));
            PrimaryLabel.Text = L.Ok;
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
