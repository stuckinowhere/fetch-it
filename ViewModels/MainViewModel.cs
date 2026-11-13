using System.Collections.ObjectModel;
using System.Diagnostics;
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
    Task<string?> AskQualityAsync(string title, IReadOnlyList<MediaQuality> qualities);
    Task<bool?> AskIncludeVideosAsync(int images, int videos);
    Task<bool> AskPasteLinkAsync(string link);
    Task ShowAccountAsync();
}

public partial class MainViewModel : ObservableObject
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
        RefreshAccount();
    }

    public IUiHost? Ui { get; set; }

    public ObservableCollection<PreviewCard> PreviewCards { get; } = [];
    [ObservableProperty] private IReadOnlyList<PreviewRow> _previewRows = [];
    private int _columns = 1;

    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(IsSinglePreview))]
    private bool _hasPreview;
    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private string _folderLabel = L.Downloads;
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

    /// <summary>Inline result line under the preview: "Saved to Downloads." plus Open folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenFolder))]
    private bool _showStatus;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenFolder))]
    private bool _statusIsError;

    [ObservableProperty] private bool _showSeparate;
    private IReadOnlyList<string> _savedNames = [];
    private string _savedFolder = "";

    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private double _tileWidth = 320;
    [ObservableProperty] private double _tileImageHeight = 180;

    public MediaProbe? Probe { get; private set; }
    public bool ShowEmptyHint => !HasPreview && !IsProbing && !IsBusy;
    public bool IsSinglePreview => HasPreview && PreviewCards.Count == 1;
    public bool HasManyPreviews => PreviewCards.Count > 1;
    public PreviewCard? Hero => PreviewCards.Count == 1 ? PreviewCards[0] : null;
    public string FetchLabel => IsProbing ? L.Stop : L.Fetch;
    public string DownloadLabel => IsBusy ? L.Stop : L.Download;
    public bool CanDownload => HasPreview && Probe is not null && !IsProbing;
    public bool ShowOpenFolder => ShowStatus && !StatusIsError;

    partial void OnUrlChanged(string value)
    {
        _probeCts?.Cancel();
        _savedFolder = "";
        ClearResult();
        HideWork();
        HideStatus();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        if (Application.Current is { } app)
            IsDark = ThemeStore.Toggle(app);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var folder = _savedFolder.Length > 0 ? _savedFolder : FolderPath;
            if (!Directory.Exists(folder))
                return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Open folder failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ShowAccountAsync()
    {
        if (Ui is null)
            return;
        await Ui.ShowAccountAsync();
        RefreshAccount();
    }

    private void RefreshAccount()
        => IsSignedIn = SessionCookies.GetStatus().State == SessionState.Active;

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
        catch (Exception ex)
        {
            AppLog.Warn($"Update check failed: {ex.Message}");
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
        _savedFolder = "";
        FolderLabel = FolderDisplay(value);
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
            if (!FolderStore.CanWrite(picked))
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

        if (!MediaRouter.TryParseHttpUrl(Url, out _))
        {
            await FailAsync("Not a link.");
            return;
        }

        var version = Interlocked.Increment(ref _probeVersion);
        var previous = _probeCts;
        previous?.Cancel();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        previous?.Dispose();

        var requested = Url.Trim();
        IsProbing = true;
        HideStatus();
        ShowWork("Reading link…", indeterminate: true);
        AppLog.Info($"fetch {AppLog.SafeUrl(Url)}");
        try
        {
            var probe = await ProbeWithSessionAsync(requested, cts.Token);
            // A partial list comes back after a Stop too, which a new link also triggers.
            if (version != _probeVersion || Url.Trim() != requested)
                return;
            ApplyProbe(probe);
            HideWork();
        }
        catch (OperationCanceledException)
        {
            if (version != _probeVersion)
                return;
            HideWork();
            IsProbing = false;
            if (!cts.IsCancellationRequested)
                await FailAsync(ProbeTimeoutMessage(Url));
        }
        catch (Exception ex)
        {
            if (version != _probeVersion)
                return;
            AppLog.Error($"fetch failed for {AppLog.SafeUrl(Url)}", ex);
            ClearResult();
            HideWork();
            IsProbing = false;
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

        try
        {
            if (await ChooseMediaAsync(probe, Url) is not { } chosen)
                return;
            probe = chosen;
        }
        catch (Exception ex)
        {
            await FailAsync(Short(ex.Message));
            return;
        }

        var duplicate = ChoiceFor(probe);
        var target = SaveFolderFor(FolderPath, Url);
        var existing = SaveClash.ExistingNames(target, probe);
        if (existing.Count > 0)
            AppLog.Info($"already saved: {existing.Count} of {probe.FileCount}; choice {duplicate}");
        if (IsAllSaved(probe, existing.Count, duplicate))
        {
            _savedFolder = target;
            ShowOk(L.Format("Already saved in {0}.", FolderDisplay(target)));
            OfferSeparate(probe);
            return;
        }

        IsBusy = true;
        HideStatus();
        ShowWork("Saving…", indeterminate: true);

        var previous = _fetchCts;
        previous?.Cancel();
        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        previous?.Dispose();
        var token = cts.Token;
        AppLog.Info($"download {AppLog.SafeUrl(Url)} -> {target}");

        try
        {
            var note = await _fetcher.DownloadAsync(probe, Url.Trim(), target, UiProgress(), duplicate, token);
            HideWork();
            _savedFolder = target;
            ShowOk(note is null ? L.Format("Saved to {0}.", FolderDisplay(target)) : L.Translate(note));
            OfferSeparate(probe);
        }
        catch (OperationCanceledException)
        {
            HideWork();
        }
        catch (Exception ex)
        {
            AppLog.Error($"download failed for {AppLog.SafeUrl(Url)}", ex);
            HideWork();
            await FailAsync(Short(ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// A name from the site's own id is the same file, so a saved one is not
    /// fetched again: a repeat run of a big profile only brings what is new. A
    /// name taken from a title proves nothing, so that file keeps both copies
    /// unless the bytes match.
    /// </summary>
    internal static DuplicateChoice ChoiceFor(MediaProbe probe)
        => SaveClash.NamesIdentifyFiles(probe) ? DuplicateChoice.Skip : DuplicateChoice.KeepBoth;

    internal static bool IsAllSaved(MediaProbe probe, int existing, DuplicateChoice duplicate)
        => duplicate == DuplicateChoice.Skip && probe.Items.Count > 0 && existing >= probe.Items.Count;

    /// <summary>Asks about videos when the list has both kinds. Null means the user cancelled.</summary>
    internal async Task<MediaProbe?> ChooseMediaAsync(MediaProbe probe, string url)
    {
        if (Ui is null || !MediaFetcher.CanLeaveOutVideos(probe, url))
            return probe;

        return await Ui.AskIncludeVideosAsync(probe.ImageCount, probe.VideoCount) switch
        {
            true => probe,
            false => probe.PhotosOnly(),
            null => null
        };
    }

    /// <summary>A whole profile gets its own folder, so it never mixes with other downloads.</summary>
    internal static string SaveFolderFor(string folder, string url)
        => MediaRouter.InstagramProfileName(url) is { } user
            ? Path.Combine(folder, MediaRouter.SanitizeFolderName(user))
            : folder;

    // Only what this download saved: the folder also holds the user's other files.
    private void OfferSeparate(MediaProbe probe)
    {
        _savedNames = SaveClash.ExistingNames(_savedFolder, probe);
        ShowSeparate = _savedNames.Count > 1;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SeparateAsync()
    {
        if (IsBusy || _savedNames.Count == 0)
            return;

        var previous = _fetchCts;
        previous?.Cancel();
        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        previous?.Dispose();

        IsBusy = true;
        HideStatus();
        ShowWork("Getting the people detector…", indeterminate: true);
        try
        {
            var outcome = await PeopleSorter.SeparateAsync(_savedFolder, _savedNames, UiProgress(), cts.Token);
            HideWork();
            var message = L.Format("Moved to {0}: {1}.", L.WithoutPeople, outcome.Moved);
            ShowOk(outcome.Unread > 0 ? $"{message} {L.Format("Could not read: {0}.", outcome.Unread)}" : message);
        }
        catch (OperationCanceledException)
        {
            HideWork();
        }
        catch (Exception ex)
        {
            AppLog.Error("people check failed", ex);
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

    /// <summary>A link dropped onto the window: fill the box and start reading it.</summary>
    public Task AcceptDroppedLinkAsync(string? text)
    {
        if (!MediaRouter.TryParseHttpUrl(text, out _) || IsBusy)
            return Task.CompletedTask;
        Url = text!.Trim();
        return FetchCommand.ExecuteAsync(null);
    }

    internal static string ShortLink(string link)
    {
        var text = link.Trim();
        return text.Length <= 72 ? text : text[..69].TrimEnd() + "…";
    }

    private async Task<MediaProbe> ProbeWithSessionAsync(string url, CancellationToken token)
    {
        // The time limit covers reading the link only: signing in (with a 2FA
        // code, say) can take as long as the user needs.
        await EnsureInstagramSessionAsync(url, token);
        try
        {
            return await ProbeTimedAsync(url, token);
        }
        catch (InvalidOperationException ex) when (
            ex.Message == MediaRouter.InstagramSessionMessage
            && MediaRouter.TryParseHttpUrl(url, out var uri)
            && MediaRouter.IsInstagram(uri))
        {
            SessionCookies.SignOut();
            RefreshAccount();
            await EnsureInstagramSessionAsync(url, token, forceSignIn: true);
            return await ProbeTimedAsync(url, token);
        }
    }

    private async Task<MediaProbe> ProbeTimedAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(ProbeSeconds(url)));
        return await _fetcher.ProbeAsync(url, UiProgress(), timeout.Token);
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
        RefreshAccount();
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
            ProgressText = L.Translate(update.Status);
    });

    private void ShowWork(string status, bool indeterminate)
    {
        ShowProgress = true;
        ProgressIsIndeterminate = indeterminate;
        if (indeterminate)
            Progress = 0;
        ProgressText = L.Translate(status);
    }

    private void HideWork()
    {
        ShowProgress = false;
        ProgressIsIndeterminate = false;
        Progress = 0;
        ProgressText = "";
    }

    private void ShowOk(string message)
    {
        StatusText = message;
        StatusIsError = false;
        ShowStatus = true;
    }

    private void HideStatus()
    {
        ShowStatus = false;
        ShowSeparate = false;
        StatusText = "";
        StatusIsError = false;
    }

    internal void ApplyProbe(MediaProbe probe)
    {
        Probe = probe;
        Title = probe.Title;
        Summary = probe.Summary;
        ReplaceCards(probe.Items);
        HasPreview = PreviewCards.Count > 0;
        NotifyPreviewLayout();
    }

    private void ClearResult()
    {
        // Every key typed in the link box lands here, mostly with nothing to clear.
        if (Probe is null && !HasPreview && PreviewCards.Count == 0)
            return;
        Probe = null;
        Title = "";
        Summary = "";
        ReplaceCards([]);
        HasPreview = false;
        NotifyPreviewLayout();
    }

    private void ReplaceCards(IReadOnlyList<MediaItem> items)
    {
        foreach (var card in PreviewCards)
            card.Dispose();
        PreviewCards.Clear();
        var tiles = items.Count > 1;
        foreach (var item in items)
            PreviewCards.Add(new PreviewCard(item, tiles));
        PreviewRows = PreviewRow.Split(PreviewCards, _columns);
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
            if (cols != _columns)
            {
                _columns = cols;
                PreviewRows = PreviewRow.Split(PreviewCards, cols);
            }

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

    internal static string FolderDisplay(string path)
    {
        var downloads = FolderStore.WindowsDownloads();
        if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), downloads.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return L.Downloads;
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
            ? name
            : path;
    }

    private async Task FailAsync(string message)
    {
        if (Ui is null)
            return;
        await Ui.ShowAlertAsync(L.Error, L.Translate(message), isError: true);
    }

    // A listing ends by itself or by Stop, and keeps what it found either way.
    private const int WholeProfileSeconds = 30 * 24 * 60 * 60;

    internal static int ProbeSeconds(string url)
    {
        if (!MediaRouter.TryParseHttpUrl(url, out var uri))
            return 35;
        if (MediaRouter.IsGofile(uri))
            return 8;
        if (MediaRouter.IsKemono(uri))
            return 60;
        if (MediaRouter.InstagramProfileUrl(url) is not null)
            return WholeProfileSeconds;
        if (MediaRouter.IsGalleryHost(uri))
            return (int)GalleryDlService.ReadLimit.TotalSeconds; // Every file is listed.
        return 35;
    }

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
