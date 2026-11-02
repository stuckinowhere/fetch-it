using System.Security.Cryptography;
using System.Text;
using FetchIt.Services;

namespace FetchIt.Tests;

public class ToolBootstrapperTests
{
    private const string Zero64 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string PinsJson(
        string ytDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe",
        string ffmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg.zip",
        string galleryUrl = "https://codeberg.org/mikf/gallery-dl/releases/download/v1.32.19/gallery-dl.exe",
        string hash = Zero64,
        int version = 1)
        => $$"""
            {
              "version": {{version}},
              "ytDlp": { "url": "{{ytDlpUrl}}", "sha256": "{{hash}}" },
              "ffmpegZip": { "url": "{{ffmpegUrl}}", "sha256": "{{hash}}" },
              "ffmpegExe": { "sha256": "{{hash}}" },
              "ffprobeExe": { "sha256": "{{hash}}" },
              "galleryDlExe": { "url": "{{galleryUrl}}", "sha256": "{{hash}}" }
            }
            """;

    [Fact]
    public void Pins_load_from_embedded_table()
    {
        var pins = ToolPins.Embedded;
        Assert.True(pins.Version >= 1);
        Assert.All(pins.Downloads, pin => Assert.True(ToolPins.IsAllowedToolUrl(pin.Url), pin.Url));
        Assert.EndsWith("gallery-dl.exe", pins.GalleryDlExe.Url, StringComparison.Ordinal);
        Assert.All(pins.AllHashes, hash => Assert.Equal(64, hash.Length));
    }

    [Fact]
    public void Embedded_public_key_is_a_p256_spki()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ToolPins.PublicKeyBase64), out var read);
        Assert.Equal(256, ecdsa.KeySize);
        Assert.True(read > 0);
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
            Assert.False(ToolBootstrapper.Sha256Equals(path, Zero64));
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

    [Theory]
    [InlineData("https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe", true)]
    [InlineData("https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip", true)]
    [InlineData("https://codeberg.org/mikf/gallery-dl/releases/download/v1.32.19/gallery-dl.exe", true)]
    [InlineData("https://GITHUB.com/yt-dlp/yt-dlp/releases/download/x/yt-dlp.exe", true)]
    [InlineData("http://github.com/yt-dlp/yt-dlp/releases/download/x/yt-dlp.exe", false)]
    [InlineData("https://github.com/someone-else/yt-dlp/releases/download/x/yt-dlp.exe", false)]
    [InlineData("https://github.com/yt-dlp/yt-dlp-fork/releases/download/x/yt-dlp.exe", false)]
    [InlineData("https://github.com/yt-dlp/yt-dlp/releases/download/../../../evil/releases/download/x/yt-dlp.exe", false)]
    [InlineData("https://user@github.com/yt-dlp/yt-dlp/releases/download/x/yt-dlp.exe", false)]
    [InlineData("https://files.pythonhosted.org/packages/x/gallery_dl.whl", false)]
    [InlineData("https://evil.example/yt-dlp.exe", false)]
    [InlineData("https://github.com.evil.example/yt-dlp/yt-dlp/releases/download/x/yt-dlp.exe", false)]
    public void Tool_urls_must_sit_under_known_release_paths(string url, bool allowed)
        => Assert.Equal(allowed, ToolPins.IsAllowedToolUrl(url));

    [Fact]
    public void Parse_accepts_a_well_formed_table()
    {
        var table = ToolPinTable.Parse(PinsJson());
        Assert.Equal(1, table.Version);
        Assert.Equal(Zero64, table.FfprobeExe.Sha256);
    }

    [Fact]
    public void Parse_rejects_offsite_or_malformed_tables()
    {
        Assert.Throws<InvalidOperationException>(() => ToolPinTable.Parse(PinsJson(ytDlpUrl: "https://evil.example/yt-dlp.exe")));
        Assert.Throws<InvalidOperationException>(() => ToolPinTable.Parse(PinsJson(ytDlpUrl: "https://github.com/other/yt-dlp/releases/download/x/yt-dlp.exe")));
        Assert.Throws<InvalidOperationException>(() => ToolPinTable.Parse(PinsJson(hash: "zz")));
        Assert.Throws<InvalidOperationException>(() => ToolPinTable.Parse(PinsJson(version: 0)));
        Assert.Throws<InvalidOperationException>(() => ToolPinTable.Parse("""{ "ytDlp": null }"""));
        Assert.Throws<InvalidOperationException>(() => ToolPinTable.Parse(PinsJson().Replace("\"ffprobeExe\": { \"sha256\": \"" + Zero64 + "\" },", "")));
    }

    [Fact]
    public void Decide_adopts_only_a_strictly_newer_table()
    {
        var current = ToolPinTable.Parse(PinsJson(version: 3));
        Assert.Equal(PinDecision.Same, ToolPins.Decide(current, ToolPinTable.Parse(PinsJson(version: 3))));
        Assert.Equal(PinDecision.Newer, ToolPins.Decide(current, ToolPinTable.Parse(PinsJson(version: 4, hash: new string('1', 64)))));
        // Same version, different content: a replay or an unversioned edit.
        Assert.Equal(PinDecision.Stale, ToolPins.Decide(current, ToolPinTable.Parse(PinsJson(version: 3, hash: new string('1', 64)))));
        // Lower version: a downgrade to an older (possibly vulnerable) tool.
        Assert.Equal(PinDecision.Stale, ToolPins.Decide(current, ToolPinTable.Parse(PinsJson(version: 2, hash: new string('1', 64)))));
    }

    [Fact]
    public void Signature_verifies_good_and_rejects_tampered_or_missing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var data = Encoding.UTF8.GetBytes(PinsJson());
        var sig = key.SignData(data, HashAlgorithmName.SHA256);

        Assert.True(ToolPins.VerifySignature(data, sig, publicKey));

        var tampered = Encoding.UTF8.GetBytes(PinsJson(version: 2));
        Assert.False(ToolPins.VerifySignature(tampered, sig, publicKey));

        var flipped = (byte[])sig.Clone();
        flipped[10] ^= 0x01;
        Assert.False(ToolPins.VerifySignature(data, flipped, publicKey));

        Assert.False(ToolPins.VerifySignature(data, Array.Empty<byte>(), publicKey));

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(ToolPins.VerifySignature(data, sig, Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void Repository_pins_are_signed_with_the_embedded_key()
    {
        // The checked-in file must verify with the key compiled into the app,
        // otherwise clients would refuse the next remote refresh.
        var root = FindRepoRoot();
        var json = File.ReadAllBytes(Path.Combine(root, "scripts", "tool-pins.json"));
        var sig = File.ReadAllText(Path.Combine(root, "scripts", "tool-pins.json.sig"));
        Assert.True(ToolPins.VerifySignature(json, sig));
        Assert.False(ToolPins.VerifySignature(json.Concat(" "u8.ToArray()).ToArray(), sig));
    }

    [Fact]
    public void TryFind_requires_every_binary_to_match_its_hash()
    {
        var root = Path.Combine(Path.GetTempPath(), "fetchit-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new Dictionary<string, byte[]>
            {
                ["yt-dlp.exe"] = "yt"u8.ToArray(),
                ["ffmpeg.exe"] = "ffmpeg"u8.ToArray(),
                ["ffprobe.exe"] = "ffprobe"u8.ToArray(),
                ["gallery-dl.exe"] = "gallery"u8.ToArray()
            };
            foreach (var (name, bytes) in files)
                File.WriteAllBytes(Path.Combine(root, name), bytes);
            string Hash(string name) => Convert.ToHexString(SHA256.HashData(files[name]));

            var pins = new ToolPinTable
            {
                Version = 1,
                YtDlp = new ToolPin { Url = "https://github.com/yt-dlp/yt-dlp/releases/download/x/yt-dlp.exe", Sha256 = Hash("yt-dlp.exe") },
                FfmpegZip = new ToolPin { Url = "https://github.com/GyanD/codexffmpeg/releases/download/x/f.zip", Sha256 = Zero64 },
                FfmpegExe = new ToolHash { Sha256 = Hash("ffmpeg.exe") },
                FfprobeExe = new ToolHash { Sha256 = Hash("ffprobe.exe") },
                GalleryDlExe = new ToolPin { Url = "https://codeberg.org/mikf/gallery-dl/releases/download/x/gallery-dl.exe", Sha256 = Hash("gallery-dl.exe") }
            };

            var found = ToolBootstrapper.TryFind(pins, [root]);
            Assert.NotNull(found);
            Assert.Equal(root, found!.FfmpegDir);
            Assert.Equal(Path.Combine(root, "gallery-dl.exe"), found.GalleryDl);

            // A planted ffprobe.exe (any size) must disqualify the whole root.
            File.WriteAllBytes(Path.Combine(root, "ffprobe.exe"), new byte[9 * 1024 * 1024]);
            File.SetLastWriteTimeUtc(Path.Combine(root, "ffprobe.exe"), DateTime.UtcNow.AddSeconds(5));
            Assert.Null(ToolBootstrapper.TryFind(pins, [root]));

            // Restore ffprobe, plant a large ffmpeg: size is no longer enough.
            File.WriteAllBytes(Path.Combine(root, "ffprobe.exe"), files["ffprobe.exe"]);
            File.SetLastWriteTimeUtc(Path.Combine(root, "ffprobe.exe"), DateTime.UtcNow.AddSeconds(10));
            File.WriteAllBytes(Path.Combine(root, "ffmpeg.exe"), new byte[9 * 1024 * 1024]);
            File.SetLastWriteTimeUtc(Path.Combine(root, "ffmpeg.exe"), DateTime.UtcNow.AddSeconds(10));
            Assert.Null(ToolBootstrapper.TryFind(pins, [root]));

            // Missing gallery-dl only drops that engine.
            File.WriteAllBytes(Path.Combine(root, "ffmpeg.exe"), files["ffmpeg.exe"]);
            File.SetLastWriteTimeUtc(Path.Combine(root, "ffmpeg.exe"), DateTime.UtcNow.AddSeconds(15));
            File.Delete(Path.Combine(root, "gallery-dl.exe"));
            var noGallery = ToolBootstrapper.TryFind(pins, [root]);
            Assert.NotNull(noGallery);
            Assert.Null(noGallery!.GalleryDl);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FetchIt.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("FetchIt.sln not found above test output.");
    }
}
