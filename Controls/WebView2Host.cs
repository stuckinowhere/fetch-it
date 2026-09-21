using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using FetchIt.Services;
using Microsoft.Web.WebView2.Core;

namespace FetchIt.Controls;

public sealed class WebView2Host : NativeControlHost
{
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _ready.Task;
    public CoreWebView2? Core => _controller?.CoreWebView2;

    public string UserDataFolder { get; set; } = SessionCookies.WebViewUserData;
    public string[] AllowedHostSuffixes { get; set; } = ["instagram.com", "instagr.am"];

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
            return base.CreateNativeControlCore(parent);

        _hwnd = Native.CreateWindowEx(
            0,
            "Static",
            "",
            Native.WsChild | Native.WsVisible,
            0,
            0,
            1,
            1,
            parent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            _ready.TrySetException(new InvalidOperationException("Could not create the sign-in window."));
            return base.CreateNativeControlCore(parent);
        }
        _ = AttachAsync();
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            _controller?.Close();
        }
        catch
        {
        }

        _controller = null;
        if (_hwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Fit();
    }

    public void Fit()
    {
        if (_controller is null || _hwnd == IntPtr.Zero)
            return;
        if (!Native.GetClientRect(_hwnd, out var rect))
            return;
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
    }

    private async Task AttachAsync()
    {
        try
        {
            Directory.CreateDirectory(UserDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder).ConfigureAwait(true);
            _controller = await env.CreateCoreWebView2ControllerAsync(_hwnd).ConfigureAwait(true);
            var core = _controller.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            Fit();
            _ready.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!HostAllowed(e.Uri))
            e.Cancel = true;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (HostAllowed(e.Uri) && Core is { } core)
            core.Navigate(e.Uri);
    }

    internal bool HostAllowed(string? url) => HostIsAllowed(url, AllowedHostSuffixes);

    internal static bool HostIsAllowed(string? url, IEnumerable<string> suffixes)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme == "about")
            return true;
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www."))
            host = host[4..];
        return suffixes.Any(suffix =>
            host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal));
    }

    private static class Native
    {
        public const int WsChild = 0x40000000;
        public const int WsVisible = 0x10000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
