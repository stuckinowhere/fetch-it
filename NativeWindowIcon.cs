using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace FetchIt;

internal static class NativeWindowIcon
{
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int ImageIcon = 1;
    private const int LrLoadFromFile = 0x0010;
    private const int GclpHIcon = -14;
    private const int GclpHIconSm = -34;
    private const int DwmwaForceIconicRepresentation = 7;

    private static IntPtr _small;
    private static IntPtr _big;

    public static void Bind(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        void ApplyNow() => Apply(window);
        window.Opened += (_, _) => ApplyNow();
        ApplyNow();
        foreach (var delay in new[] { 300, 1000, 2500, 6000 })
            DispatcherTimer.RunOnce(ApplyNow, TimeSpan.FromMilliseconds(delay));
    }

    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;

        EnsureIcons();
        if (_small != IntPtr.Zero)
        {
            SendMessage(hwnd, WmSetIcon, IconSmall, _small);
            SetClassLongPtr(hwnd, GclpHIconSm, _small);
        }

        if (_big != IntPtr.Zero)
        {
            SendMessage(hwnd, WmSetIcon, IconBig, _big);
            SetClassLongPtr(hwnd, GclpHIcon, _big);
        }

        var forceIcon = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaForceIconicRepresentation, ref forceIcon, sizeof(int));
    }

    public static void SetProcessAppId()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID("WASD.FetchIt");
        }
        catch
        {
        }
    }

    private static void EnsureIcons()
    {
        if (_small != IntPtr.Zero && _big != IntPtr.Zero)
            return;

        var ico = FindSidecarIco();
        if (ico is not null)
        {
            _small = LoadImage(IntPtr.Zero, ico, ImageIcon, 16, 16, LrLoadFromFile);
            _big = LoadImage(IntPtr.Zero, ico, ImageIcon, 32, 32, LrLoadFromFile);
        }

        var exe = Environment.ProcessPath;
        if (_small == IntPtr.Zero && exe is not null)
            _small = ExtractIcon(IntPtr.Zero, exe, 0);
        if (_big == IntPtr.Zero && exe is not null)
            _big = ExtractIcon(IntPtr.Zero, exe, 0);
    }

    internal static string? FindSidecarIco()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WasdFetchIt",
            "FetchIt.ico");

        foreach (var candidate in new[]
                 {
                     dir is null ? null : Path.Combine(dir, "FetchIt.ico"),
                     dir is null ? null : Path.Combine(dir, "fetchit.ico"),
                     local
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, int type, int cx, int cy, int fuLoad);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
