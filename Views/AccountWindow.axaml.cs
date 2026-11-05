using Avalonia.Controls;
using Avalonia.Interactivity;
using FetchIt.Services;

namespace FetchIt.Views;

public partial class AccountWindow : Window
{
    private readonly CancellationTokenSource _closing = new();

    public AccountWindow()
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
        Closed += (_, _) => _closing.Cancel(); // Closing here also closes the sign-in Chrome.
        Refresh();
    }

    private void Refresh()
    {
        var status = SessionCookies.GetStatus();
        Status.Text = Describe(status);
        PrimaryLabel.Text = status.State == SessionState.Active ? L.SignOut : L.SignIn;
    }

    internal static string Describe(SessionStatus status) => status.State switch
    {
        SessionState.Active when status.Expires is { } expires
            => L.Format("Signed in until {0}", expires.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture)),
        SessionState.Active => L.T("Signed in"),
        SessionState.Expired => L.T("Session expired. Sign in again."),
        _ => L.T("Not signed in")
    };

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        Primary.IsEnabled = false;
        try
        {
            if (SessionCookies.GetStatus().State == SessionState.Active)
            {
                SessionCookies.SignOut();
            }
            else
            {
                SessionCookies.Clear();
                Status.Text = L.T("Finish signing in in the window that opened.");
                await InstagramSignInFlow.RunAsync(this, _closing.Token);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Account action failed", ex);
        }
        finally
        {
            if (!_closing.IsCancellationRequested)
            {
                Primary.IsEnabled = true;
                Refresh();
            }
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
