using System.Runtime.InteropServices;
using System.Text.Json;

namespace FetchIt.Services;

public static class FolderStore
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WasdFetchIt",
        "folder.json");

    public static string Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.TryGetProperty("path", out var path)
                    && path.GetString() is { Length: > 0 } saved
                    && Directory.Exists(saved))
                    return saved;
            }
        }
        catch
        {
        }

        return WindowsDownloads();
    }

    public static bool CanWrite(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder))
                return false;
            var dir = Path.GetFullPath(folder);
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".fetchit-write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, [0x20]);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new { path }));
    }

    public static string WindowsDownloads()
    {
        var known = KnownDownloadsFolder();
        if (!string.IsNullOrEmpty(known) && Directory.Exists(known))
            return known;
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(user, "Downloads");
        return Directory.Exists(downloads) ? downloads : user;
    }

    private static string? KnownDownloadsFolder()
    {
        var id = DownloadsFolderId;
        var hr = SHGetKnownFolderPath(in id, 0, 0, out var pPath);
        if (hr != 0 || pPath == 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(pPath);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pPath);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        in Guid rfid,
        uint dwFlags,
        nint hToken,
        out nint ppszPath);
}
