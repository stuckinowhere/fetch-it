using Avalonia.Controls;
using Avalonia.Interactivity;
using FetchIt.Services;

namespace FetchIt.Views;

public partial class AccountWindow : Window
{
    public AccountWindow()
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
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
                await new InstagramLoginWindow().ShowDialog<bool>(this);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Account action failed", ex);
        }
        finally
        {
            Primary.IsEnabled = true;
            Refresh();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
