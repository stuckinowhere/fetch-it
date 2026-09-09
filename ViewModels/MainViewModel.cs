using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.ViewModels;

public interface IUiHost
{
    Task<string?> PickFolderAsync();
    Task<string?> ReadClipboardAsync();
}

public partial class MainViewModel : ViewModelBase
{
    private readonly MediaFetcher _fetcher;
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _fetchCts;
    private int _probeVersion;

    public MainViewModel() : this(new MediaFetcher())
    {
    }

    public MainViewModel(MediaFetcher fetcher)
    {
        _fetcher = fetcher;
        FolderPath = DefaultDownloads();
    }

    public IUiHost? Ui { get; set; }

    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _showVideo = true;
    [ObservableProperty] private bool _showLogin;
    [ObservableProperty] private bool _useCookies;
    [ObservableProperty] private string _loginLabel = "off";
    [ObservableProperty] private string _qualityLabel = VideoQualityText.Label(VideoQuality.Best);
    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private string _folderLabel = "Downloads";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _actionLabel = "Fetch";

    public VideoQuality Quality { get; private set; } = VideoQuality.Best;
    public MediaProbe? Probe { get; private set; }

    partial void OnUrlChanged(string value)
    {
        Error = "";
        if (MediaRouter.TryParseHttpUrl(value, out var uri))
            ShowLogin = MediaRouter.NeedsLoginRow(uri);
        else
            ShowLogin = false;
        _ = ProbeSoonAsync();
    }

    partial void OnUseCookiesChanged(bool value)
    {
        LoginLabel = value ? "on" : "off";
        if (MediaRouter.TryParseHttpUrl(Url, out _))
            _ = ProbeSoonAsync();
    }

    [RelayCommand]
    private void ToggleLogin() => UseCookies = !UseCookies;

    partial void OnFolderPathChanged(string value) => FolderLabel = FolderDisplay(value);

    [RelayCommand]
    private void CycleQuality()
    {
        Quality = VideoQualityText.Next(Quality);
        QualityLabel = VideoQualityText.Label(Quality);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (Ui is null)
            return;
        var picked = await Ui.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(picked))
            FolderPath = picked;
    }

    [RelayCommand]
    private async Task FetchOrStopAsync()
    {
        if (IsBusy)
        {
            _fetchCts?.Cancel();
            return;
        }

        Error = "";
        if (!MediaRouter.TryParseHttpUrl(Url, out _))
        {
            Error = "Not a link.";
            return;
        }

        IsBusy = true;
        ActionLabel = "Stop";
        ShowProgress = true;
        Progress = 0;
        ProgressText = "";

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var token = _fetchCts.Token;

        try
        {
            var probe = Probe ?? await _fetcher.ProbeAsync(Url.Trim(), UseCookies, token);
            ApplyProbe(probe);
            var progress = new Progress<FetchProgress>(update =>
            {
                Progress = update.Percent;
                ProgressText = update.Status;
            });
            await _fetcher.DownloadAsync(probe, Url.Trim(), FolderPath, Quality, UseCookies, progress, token);
            Progress = 100;
            ProgressText = "done";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "";
            ShowProgress = false;
        }
        catch (Exception ex)
        {
            Error = Short(ex.Message);
            if (Error.Contains("login", StringComparison.OrdinalIgnoreCase))
                ShowLogin = true;
        }
        finally
        {
            IsBusy = false;
            ActionLabel = "Fetch";
        }
    }

    public async Task PasteIfEmptyAsync()
    {
        if (!string.IsNullOrWhiteSpace(Url) || Ui is null)
            return;
        var clip = await Ui.ReadClipboardAsync();
        if (MediaRouter.TryParseHttpUrl(clip, out _))
            Url = clip!.Trim();
    }

    private async Task ProbeSoonAsync()
    {
        var version = Interlocked.Increment(ref _probeVersion);
        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;

        try
        {
            await Task.Delay(350, token);
            if (version != _probeVersion)
                return;
            if (!MediaRouter.TryParseHttpUrl(Url, out _))
            {
                ClearResult();
                return;
            }

            var probe = await _fetcher.ProbeAsync(Url.Trim(), UseCookies, token);
            if (version != _probeVersion)
                return;
            ApplyProbe(probe);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version != _probeVersion)
                return;
            ClearResult();
            Error = Short(ex.Message);
            if (Error.Contains("login", StringComparison.OrdinalIgnoreCase) && MediaRouter.TryParseHttpUrl(Url, out var uri))
                ShowLogin = MediaRouter.NeedsLoginRow(uri) || true;
        }
    }

    private void ApplyProbe(MediaProbe probe)
    {
        Probe = probe;
        Title = probe.Title;
        Summary = probe.Summary;
        HasResult = true;
        ShowVideo = probe.HasVideo;
        if (probe.NeedsLogin || (MediaRouter.TryParseHttpUrl(Url, out var uri) && MediaRouter.NeedsLoginRow(uri)))
            ShowLogin = true;
        Error = "";
    }

    private void ClearResult()
    {
        Probe = null;
        Title = "";
        Summary = "";
        HasResult = false;
        ShowVideo = true;
    }

    internal static string DefaultDownloads()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(user, "Downloads");
        return Directory.Exists(downloads) ? downloads : user;
    }

    internal static string FolderDisplay(string path)
    {
        var downloads = DefaultDownloads();
        if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), downloads.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return "Downloads";
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
            ? name
            : path;
    }

    private static string Short(string message)
    {
        var line = message.Replace('\n', ' ').Trim();
        return line.Length > 72 ? line[..72].Trim() : line;
    }
}
