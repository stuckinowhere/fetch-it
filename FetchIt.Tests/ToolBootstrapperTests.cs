using System.Security.Cryptography;
using System.Text;
using FetchIt.Services;

namespace FetchIt.Tests;

public class ToolBootstrapperTests
{
    [Fact]
    public void Pins_load_from_embedded_table()
    {
        var pins = ToolPins.Embedded;
        Assert.StartsWith("https://", pins.YtDlp.Url, StringComparison.Ordinal);
        Assert.StartsWith("https://", pins.FfmpegZip.Url, StringComparison.Ordinal);
        Assert.StartsWith("https://", pins.GalleryDlExe.Url, StringComparison.Ordinal);
        Assert.EndsWith("gallery-dl.exe", pins.GalleryDlExe.Url, StringComparison.Ordinal);
        Assert.Equal(64, pins.YtDlp.Sha256.Length);
        Assert.Equal(64, pins.FfmpegZip.Sha256.Length);
        Assert.Equal(64, pins.GalleryDlExe.Sha256.Length);
    }

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
    public void Sha256Equals_cache_notices_rewrites()
    {
        var path = Path.Combine(Path.GetTempPath(), "fetchit-hash-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, "one"u8.ToArray());
            var one = Convert.ToHexString(SHA256.HashData("one"u8.ToArray()));
            Assert.True(ToolBootstrapper.Sha256Equals(path, one));

            File.WriteAllBytes(path, "two!"u8.ToArray());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            Assert.False(ToolBootstrapper.Sha256Equals(path, one));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void IsUsableFfmpeg_rejects_tiny_stub()
    {
        var path = Path.Combine(Path.GetTempPath(), "fetchit-ff-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(path, new byte[4096]);
            Assert.False(ToolBootstrapper.IsUsableFfmpeg(path));
            Assert.False(ToolBootstrapper.IsUsableFfmpeg(path + ".missing"));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Theory]
    [InlineData("https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe", true)]
    [InlineData("https://codeberg.org/mikf/gallery-dl/releases/download/v1.32.19/gallery-dl.exe", true)]
    [InlineData("http://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe", false)]
    [InlineData("https://evil.example/yt-dlp.exe", false)]
    [InlineData("https://github.com.evil.example/yt-dlp.exe", false)]
    public void Remote_pins_only_accept_known_hosts(string url, bool allowed)
        => Assert.Equal(allowed, ToolPins.IsAllowedToolUrl(url));

    [Fact]
    public void Remote_pins_reject_offsite_or_malformed_tables()
    {
        var offsite = """
            {
              "ytDlp": { "url": "https://evil.example/yt-dlp.exe", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" },
              "ffmpegZip": { "url": "https://github.com/x/f.zip", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" },
              "galleryDlExe": { "url": "https://codeberg.org/x/g.exe", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }
            }
            """;
        Assert.Throws<InvalidOperationException>(() => ToolPinTable.Parse(offsite));

        var badHash = offsite.Replace("https://evil.example/yt-dlp.exe", "https://github.com/x/yt-dlp.exe")
            .Replace("0000000000000000000000000000000000000000000000000000000000000000\" }", "zz\" }");
        Assert.Throws<InvalidOperationException>(() => ToolPinTable.Parse(badHash));

        Assert.Throws<InvalidOperationException>(() => ToolPinTable.Parse("""{ "ytDlp": null }"""));
    }

    [Fact]
    public void SameAs_compares_urls_and_hashes()
    {
        var a = ToolPins.Embedded;
        var json = $$"""
            {
              "ytDlp": { "url": "{{a.YtDlp.Url}}", "sha256": "{{a.YtDlp.Sha256.ToUpperInvariant()}}" },
              "ffmpegZip": { "url": "{{a.FfmpegZip.Url}}", "sha256": "{{a.FfmpegZip.Sha256}}" },
              "galleryDlExe": { "url": "{{a.GalleryDlExe.Url}}", "sha256": "{{a.GalleryDlExe.Sha256}}" }
            }
            """;
        Assert.True(a.SameAs(ToolPinTable.Parse(json)));
        Assert.False(a.SameAs(ToolPinTable.Parse(json.Replace(a.YtDlp.Url, "https://github.com/other/yt-dlp.exe"))));
    }
}
