using Avalonia.Controls;
using Avalonia.Threading;
using FetchIt.Services;

namespace FetchIt.Views;

/// <summary>Sign in through real Chrome; use the built-in window only when Chrome cannot run.</summary>
internal static class InstagramSignInFlow
{
    public static async Task<bool> RunAsync(Window owner, CancellationToken cancellationToken)
    {
        if (ChromeSignIn.FindChrome() is { } chrome)
        {
            var result = await new ChromeSignIn(chrome, SessionCookies.ChromeProfile, ChromeSignIn.LoginUrl)
                .RunAsync(cancellationToken);
            if (result.Status == ChromeSignInStatus.SignedIn)
            {
                SessionCookies.Save(result.Cookies);
                return SessionCookies.HasUsableFile();
            }

            if (result.Status == ChromeSignInStatus.Closed)
                return false;
        }

        return await WithBuiltInWindowAsync(owner, cancellationToken);
    }

    private static async Task<bool> WithBuiltInWindowAsync(Window owner, CancellationToken cancellationToken)
    {
        var window = new InstagramLoginWindow();
        await using var close = cancellationToken.Register(() =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (window.IsVisible)
                    window.Close(false);
            });
        });
        var result = await window.ShowDialog<bool>(owner);
        return result && SessionCookies.HasUsableFile();
    }
}
