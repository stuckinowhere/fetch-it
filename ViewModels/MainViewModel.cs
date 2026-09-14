using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.ViewModels;

public interface IUiHost
{
    Task<string?> PickFolderAsync();
    Task<string?> ReadClipboardAsync();
    Task<bool> SignInInstagramAsync(CancellationToken cancellationToken);
    Task ShowUpdateAsync(UpdateCheckResult result);
    Task ShowAlertAsync(string heading, string message, bool isError);
    Task<DuplicateChoice> AskIfAlreadySavedAsync(string folderLabel, IReadOnlyList<string> names);
    Task<string?> AskQualityAsync(string title, IReadOnlyList<MediaQuality> qualities);
    Task<bool> AskPasteLinkAsync(string link);
}

public partial class MainViewModel : ViewModelBase
{
    private readonly MediaFetcher _fetcher;
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _fetchCts;
    private int _probeVersion;
    private int _checkingUpdates;
    private double _viewportWidth;
    private double _viewportHeight;
    private string? _pasteOffer;
    private bool _askingPaste;

    public MainViewModel() : this(new MediaFetcher())
    {
    }

    public MainViewModel(MediaFetcher fetcher)
    {
        _fetcher = fetcher;
        FolderPath = FolderStore.Load();
        IsDark = ThemeStore.IsDark();
    }

    public IUiHost? Ui { get; set; }

    public ObservableCollection<PreviewCard> PreviewCards { get; } = [];

    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(IsSinglePreview))]
    private bool _hasPreview;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _moreLabel = "";
    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private string _folderLabel = "Downloads";
    [ObservableProperty] private string _folderTip = "Choose save folder";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(DownloadLabel))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(FetchLabel))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isProbing;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _progressIsIndeterminate;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    private string _error = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    private string _success = "";
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private double _tileWidth = 320;
    [ObservableProperty] private double _tileImageHeight = 180;

    public MediaProbe? Probe { get; private set; }
    public bool ShowEmptyHint =>
        !HasPreview && string.IsNullOrEmpty(Error) && string.IsNullOrEmpty(Success) && !IsProbing && !IsBusy;
    public bool IsSinglePreview => HasPreview && PreviewCards.Count == 1;
    public bool HasManyPreviews => PreviewCards.Count > 1;
    public PreviewCard? Hero => PreviewCards.Count == 1 ? PreviewCards[0] : null;
    public string FetchLabel => IsProbing ? "Stop" : "Fetch";
    public string DownloadLabel => IsBusy ? "Stop" : "Download";
    public bool CanDownload => HasPreview && Probe is not null && !IsProbing;

    partial void OnUrlChanged(string value)
    {
        ClearStatus();
        _probeCts?.Cancel();
        ClearResult();
        HideWork();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        if (Application.Current is { } app)
            IsDark = ThemeStore.Toggle(app);
    }

    public Task CheckUpdatesOnLaunchAsync() => CheckForUpdatesCoreAsync(notifyWhenCurrent: false);

    [RelayCommand]
    private Task CheckForUpdatesAsync() => CheckForUpdatesCoreAsync(notifyWhenCurrent: true);

    private async Task CheckForUpdatesCoreAsync(bool notifyWhenCurrent)
    {
        if (Interlocked.CompareExchange(ref _checkingUpdates, 1, 0) != 0)
            return;

        try
        {
            using var client = new GitHubUpdateClient();
            var result = await client.CheckAsync().ConfigureAwait(true);
            if (result.Status == UpdateCheckStatus.Current && !notifyWhenCurrent)
                return;
            if (Ui is null)
                return;
            await Ui.ShowUpdateAsync(result);
        }
        catch
        {
            if (!notifyWhenCurrent || Ui is null)
                return;
            await Ui.ShowUpdateAsync(new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                AppVersion.Current,
                null,
                null,
                null,
                null,
                "Could not check for updates."));
        }
        finally
        {
            Interlocked.Exchange(ref _checkingUpdates, 0);
        }
    }

    partial void OnFolderPathChanged(string value)
    {
        FolderLabel = FolderDisplay(value);
        FolderTip = $"Save folder: {value}";
        FolderStore.Save(value);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (Ui is null)
            return;
        var picked = await Ui.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            FolderPath = picked;
            if (FolderStore.CanWrite(picked))
                ClearStatus();
            else
                await FailAsync("Windows blocked that folder. Pick another save folder.");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task FetchAsync()
    {
        if (IsBusy)
            return;
        if (IsProbing)
        {
            _probeCts?.Cancel();
            return;
        }

        ClearStatus();
        if (!MediaRouter.TryParseHttpUrl(Url, out _))
        {
            await FailAsync("Not a link.");
            return;
        }

        var version = Interlocked.Increment(ref _probeVersion);
        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_probeCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ProbeSeconds(Url)));
        var token = timeoutCts.Token;

        IsProbing = true;
        ShowWork("Reading link…", indeterminate: true);
        try
        {
            var probe = await ProbeWithSessionAsync(Url.Trim(), token);
            if (version != _probeVersion)
                return;
            ApplyProbe(probe);
            HideWork();
        }
        catch (OperationCanceledException)
        {
            if (version != _probeVersion)
                return;
            HideWork();
            if (!_probeCts.IsCancellationRequested)
                await FailAsync(ProbeTimeoutMessage(Url));
        }
        catch (Exception ex)
        {
            if (version != _probeVersion)
                return;
            ClearResult();
            HideWork();
            await FailAsync(Short(ex.Message));
        }
        finally
        {
            if (version == _probeVersion)
                IsProbing = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadAsync()
    {
        if (IsBusy)
        {
            _fetchCts?.Cancel();
            return;
        }

        if (Probe is null || !HasPreview)
        {
            await FailAsync("Fetch first.");
            return;
        }

        ClearStatus();

        var probe = Probe!;
        if (probe.Qualities.Count > 1)
        {
            if (Ui is null)
                probe = OkRuService.WithSelectedQuality(probe, probe.Qualities[0].Url);
            else
            {
                string? picked;
                try
                {
                    picked = await Ui.AskQualityAsync(probe.Title, probe.Qualities);
                }
                catch (Exception ex)
                {
                    await FailAsync(Short(ex.Message));
                    return;
                }

                if (picked is null)
                    return;
                probe = OkRuService.WithSelectedQuality(probe, picked);
            }
        }

        var existing = SaveClash.ExistingNames(FolderPath, probe);
        var duplicate = DuplicateChoice.KeepBoth;
        if (existing.Count > 0)
        {
            if (Ui is null)
                duplicate = DuplicateChoice.KeepBoth;
            else
                duplicate = await Ui.AskIfAlreadySavedAsync(FolderLabel, existing);

            if (duplicate == DuplicateChoice.Cancel)
                return;
            if (duplicate == DuplicateChoice.Skip)
            {
                await OkAsync($"Already in {FolderLabel}.");
                return;
            }
        }

        IsBusy = true;
        ShowWork("Saving…", indeterminate: true);

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var token = _fetchCts.Token;

        try
        {
            await _fetcher.DownloadAsync(probe, Url.Trim(), FolderPath, UiProgress(), duplicate, token);
            HideWork();
            await OkAsync($"Saved to {FolderLabel}.");
        }
        catch (OperationCanceledException)
        {
            HideWork();
        }
        catch (Exception ex)
        {
            HideWork();
            await FailAsync(Short(ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task PasteIfEmptyAsync()
    {
        if (!string.IsNullOrWhiteSpace(Url) || Ui is null || _askingPaste || IsBusy || IsProbing)
            return;

        var clip = await Ui.ReadClipboardAsync();
        if (!MediaRouter.TryParseHttpUrl(clip, out _))
            return;

        var link = clip!.Trim();
        if (string.Equals(link, _pasteOffer, StringComparison.Ordinal))
            return;

        _askingPaste = true;
        try
        {
            var paste = await Ui.AskPasteLinkAsync(link);
            _pasteOffer = link;
            if (paste && string.IsNullOrWhiteSpace(Url))
                Url = link;
        }
        finally
        {
            _askingPaste = false;
        }
    }

    internal static string ShortLink(string link)
    {
        var text = link.Trim();
        return text.Length <= 72 ? text : text[..69].TrimEnd() + "…";
    }

    private async Task<MediaProbe> ProbeWithSessionAsync(string url, CancellationToken token)
    {
        await EnsureInstagramSessionAsync(url, token);
        try
        {
            return await _fetcher.ProbeAsync(url, UiProgress(), token);
        }
        catch (InvalidOperationException ex) when (ex.Message == MediaRouter.InstagramSessionMessage)
        {
            SessionCookies.Clear();
            await EnsureInstagramSessionAsync(url, token, forceSignIn: true);
            return await _fetcher.ProbeAsync(url, UiProgress(), token);
        }
    }

    private async Task EnsureInstagramSessionAsync(
        string url,
        CancellationToken token,
        bool forceSignIn = false)
    {
        if (!MediaRouter.TryParseHttpUrl(url, out var uri) || !MediaRouter.IsInstagram(uri))
            return;
        if (!forceSignIn && (SessionCookies.HasUsableFile() || ChromeCookieDb.IsReadable()))
            return;
        if (Ui is null)
            return;

        ShowWork("Sign in to Instagram…", indeterminate: true);
        var signedIn = await Ui.SignInInstagramAsync(token);
        if (!signedIn)
            throw new InvalidOperationException(MediaRouter.InstagramSessionMessage);
    }

    private IProgress<FetchProgress> UiProgress() => new Progress<FetchProgress>(update =>
    {
        if (update.HasPercent || update.Percent > 0)
        {
            ProgressIsIndeterminate = false;
            Progress = update.Percent;
        }
        if (!string.IsNullOrWhiteSpace(update.Status))
            ProgressText = update.Status;
    });

    private void ShowWork(string status, bool indeterminate)
    {
        ShowProgress = true;
        ProgressIsIndeterminate = indeterminate;
        if (indeterminate)
            Progress = 0;
        ProgressText = status;
    }

    private void HideWork()
    {
        ShowProgress = false;
        ProgressIsIndeterminate = false;
        Progress = 0;
        ProgressText = "";
    }

    private void ApplyProbe(MediaProbe probe)
    {
        Probe = probe;
        Title = probe.Title;
        Summary = probe.Summary;
        HasResult = true;
        ReplaceCards(probe.PreviewItems);
        HasPreview = PreviewCards.Count > 0;
        HasMore = probe.ExtraCount > 0;
        MoreLabel = HasMore ? $"+{probe.ExtraCount} more" : "";
        ClearStatus();
        NotifyPreviewLayout();
    }

    private void ClearResult()
    {
        Probe = null;
        Title = "";
        Summary = "";
        HasResult = false;
        ReplaceCards([]);
        HasPreview = false;
        HasMore = false;
        MoreLabel = "";
        NotifyPreviewLayout();
    }

    private void ReplaceCards(IEnumerable<MediaItem> items)
    {
        foreach (var card in PreviewCards)
            card.Dispose();
        PreviewCards.Clear();
        foreach (var item in items)
            PreviewCards.Add(new PreviewCard(item));
        NotifyPreviewLayout();
    }

    public void FitPreview(double viewportWidth, double viewportHeight)
    {
        _viewportWidth = Math.Max(0, viewportWidth);
        _viewportHeight = Math.Max(0, viewportHeight);
        var width = _viewportWidth;
        var height = _viewportHeight;
        if (PreviewCards.Count <= 1)
        {
            TileWidth = Math.Max(240, width);
            TileImageHeight = Math.Max(180, height);
        }
        else
        {
            var cols = width >= 1000 ? 3 : width >= 640 ? 2 : 1;
            const double gap = 16;
            TileWidth = Math.Max(200, Math.Floor((width - gap * (cols - 1)) / cols));
            TileImageHeight = Math.Max(120, Math.Floor(TileWidth * 9.0 / 16.0));
        }

        foreach (var card in PreviewCards)
        {
            card.TileWidth = TileWidth;
            card.TileImageHeight = TileImageHeight;
        }
    }

    private void NotifyPreviewLayout()
    {
        OnPropertyChanged(nameof(IsSinglePreview));
        OnPropertyChanged(nameof(HasManyPreviews));
        OnPropertyChanged(nameof(Hero));
        if (_viewportWidth > 0 || _viewportHeight > 0)
            FitPreview(_viewportWidth, _viewportHeight);
    }

    internal static string DefaultDownloads() => FolderStore.WindowsDownloads();

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

    private void ClearStatus()
    {
        Error = "";
        Success = "";
    }

    private Task FailAsync(string message) => ShowStatusAsync(message, isError: true);

    private Task OkAsync(string message) => ShowStatusAsync(message, isError: false);

    private async Task ShowStatusAsync(string message, bool isError)
    {
        if (isError)
        {
            Success = "";
            Error = message;
        }
        else
        {
            Error = "";
            Success = message;
        }

        if (Ui is null)
            return;
        await Ui.ShowAlertAsync(isError ? "Error" : "Saved", message, isError);
    }

    internal static int ProbeSeconds(string url) =>
        MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsGofile(uri) ? 8 : 35;

    internal static string ProbeTimeoutMessage(string url) =>
        MediaRouter.TryParseHttpUrl(url, out var uri) && MediaRouter.IsGofile(uri)
            ? GofileService.UnreachableMessage
            : "That site took too long. Try again.";

    internal static string Short(string message)
    {
        if (message.Contains("invalid start of a value", StringComparison.OrdinalIgnoreCase)
            || message.Contains("BytePositionInLine", StringComparison.OrdinalIgnoreCase))
            return "Could not read that link.";
        if (message.Contains("SSL connection", StringComparison.OrdinalIgnoreCase))
            return "GoFile dropped the connection. Try again.";

        var line = message.Replace('\n', ' ').Trim();
        return line.Length > 72 ? line[..72].Trim() : line;
    }
}
