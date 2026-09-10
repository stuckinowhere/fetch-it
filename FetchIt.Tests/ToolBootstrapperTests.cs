using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FetchIt.Services;

namespace FetchIt.Tests;

public class ToolBootstrapperTests
{
    [Fact]
    public void Sha256Equals_matches_known_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), "fetchit-hash-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var bytes = Encoding.UTF8.GetBytes("fetch it");
            File.WriteAllBytes(path, bytes);
            var expected = Convert.ToHexString(SHA256.HashData(bytes));
            Assert.True(ToolBootstrapper.Sha256Equals(path, expected));
            Assert.False(ToolBootstrapper.Sha256Equals(path, new string('0', 64)));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ExtractWheel_skips_zip_slip()
    {
        var root = Path.Combine(Path.GetTempPath(), "fetchit-wheel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "payload.whl");
        var dest = Path.Combine(root, "lib");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "../pwned.txt", "escaped");
                WriteEntry(zip, "gallery_dl/ok.txt", "safe");
            }

            ToolBootstrapper.ExtractWheel(zipPath, dest);
            Assert.True(File.Exists(Path.Combine(dest, "gallery_dl", "ok.txt")));
            Assert.False(File.Exists(Path.Combine(root, "pwned.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, string contents)
    {
        var entry = zip.CreateEntry(name);
        using var stream = new StreamWriter(entry.Open());
        stream.Write(contents);
    }
}
