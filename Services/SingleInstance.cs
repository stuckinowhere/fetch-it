using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FetchIt.Services;

public sealed class SingleInstance : IDisposable
{
    public const string ActivateName = @"Local\WasdFetchIt-Activate";

    private readonly FileStream _lock;
    private readonly EventWaitHandle _activate;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _watch;

    private SingleInstance(FileStream fileLock, EventWaitHandle activate)
    {
        _lock = fileLock;
        _activate = activate;
    }

    public static string LockPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasdFetchIt",
        "instance.lock");

    public static bool TryOwn(out SingleInstance? instance)
    {
        if (TryOwn(LockPath, ActivateName, out instance))
            return true;
        TryForegroundExisting();
        return false;
    }

    internal static bool TryOwn(string lockPath, string activateName, out SingleInstance? instance)
    {
        instance = null;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        FileStream fileLock;
        try
        {
            fileLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            Signal(activateName);
            return false;
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, activateName);
        instance = new SingleInstance(fileLock, activate);
        return true;
    }

    public void Watch(Action onActivate)
    {
        if (_watch is not null)
            return;

        _watch = new Thread(() =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (_activate.WaitOne(TimeSpan.FromMilliseconds(400)))
                        onActivate();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
            Name = "WasdFetchIt-Activate"
        };
        _watch.Start();
    }

    internal static void Signal(string activateName)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(activateName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    internal static void TryForegroundExisting(string processName = "FetchIt")
    {
        if (!OperatingSystem.IsWindows())
            return;

        var current = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.Id == current)
                    continue;
                var hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    continue;
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, 9);
                _ = SetForegroundWindow(hwnd);
                return;
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _activate.Set();
        }
        catch
        {
        }

        _activate.Dispose();
        _lock.Dispose();
        _cts.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
