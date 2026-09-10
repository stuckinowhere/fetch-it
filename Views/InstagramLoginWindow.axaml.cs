using Avalonia.Controls;
using FetchIt.Services;
using Microsoft.Web.WebView2.Core;

namespace FetchIt.Views;

public partial class InstagramLoginWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _saved;

    public InstagramLoginWindow()
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        Opened += OnOpened;
        Closed += (_, _) => _cts.Cancel();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await Web.Ready.WaitAsync(_cts.Token).ConfigureAwait(true);
            var core = Web.Core;
            if (core is null)
            {
                Close(false);
                return;
            }

            core.Navigate("https://www.instagram.com/accounts/login/");
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(400, _cts.Token).ConfigureAwait(true);
                if (await TrySaveSessionAsync(core).ConfigureAwait(true))
                {
                    _saved = true;
                    Close(true);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!_saved)
                Close(false);
        }
    }

    private static async Task<bool> TrySaveSessionAsync(CoreWebView2 core)
    {
        var cookies = await core.CookieManager.GetCookiesAsync("https://www.instagram.com").ConfigureAwait(true);
        var extra = await core.CookieManager.GetCookiesAsync("https://i.instagram.com").ConfigureAwait(true);
        var rows = cookies.Concat(extra)
            .GroupBy(cookie => (cookie.Domain, cookie.Name), (key, group) => group.First())
            .Select(ToRow)
            .ToList();
        if (!rows.Any(row =>
                row.Name.Equals("sessionid", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(row.Value)
                && row.Domain.Contains("instagram", StringComparison.OrdinalIgnoreCase)))
            return false;

        SessionCookies.Save(rows);
        return SessionCookies.HasUsableFile();
    }

    private static CookieRow ToRow(CoreWebView2Cookie cookie)
    {
        long expires = 0;
        if (!cookie.IsSession && cookie.Expires > DateTime.UnixEpoch)
        {
            var utc = cookie.Expires.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(cookie.Expires, DateTimeKind.Utc)
                : cookie.Expires.ToUniversalTime();
            expires = new DateTimeOffset(utc).ToUnixTimeSeconds();
        }

        return new CookieRow(
            cookie.Domain,
            string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
            cookie.IsSecure,
            expires,
            cookie.Name,
            cookie.Value);
    }
}
