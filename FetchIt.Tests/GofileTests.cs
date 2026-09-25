using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.Tests;

public class GofileTests
{
    [Theory]
    [InlineData("https://gofile.io/d/Soimwa", "Soimwa", null)]
    [InlineData("https://www.gofile.io/d/Soimwa#secret", "Soimwa", "secret")]
    [InlineData("https://gofile.io/d/Soimwa?password=pass", "Soimwa", "pass")]
    public void Parses_folder_id_and_password(string url, string id, string? password)
    {
        Assert.True(GofileService.TryParseFolder(url, out var parsed, out var parsedPassword));
        Assert.Equal(id, parsed);
        Assert.Equal(password, parsedPassword);
        Assert.True(MediaRouter.IsGofile(new Uri(url)));
    }

    [Fact]
    public void Website_token_matches_gallery_dl()
    {
        var token = GofileService.WebsiteToken(
            GofileService.UserAgent,
            "guest-token",
            1_777_766_400);
        Assert.Equal(64, token.Length);
        Assert.Equal(
            GofileService.WebsiteToken(GofileService.UserAgent, "guest-token", 1_777_766_400 + 10),
            token);
        Assert.NotEqual(
            token,
            GofileService.WebsiteToken(GofileService.UserAgent, "guest-token", 1_777_766_400 + 14400));
        Assert.NotEqual(
            token,
            GofileService.WebsiteToken(
                GofileService.UserAgent, "guest-token", 1_777_766_400, GofileService.WebsiteSalts[1]));
    }

    [Fact]
    public void Curl_api_args_use_ipv4_and_short_timeout()
    {
        var args = GofileCdn.CurlApiArgs("https://api.gofile.io/contents/x", "tok", "wt");
        Assert.Contains("-4", args);
        Assert.Contains("--connect-timeout", args);
        Assert.Contains("2", args);
        Assert.Contains("--max-time", args);
        Assert.Contains("8", args);
        Assert.Contains("X-Website-Token: wt", args);
        Assert.Equal("https://api.gofile.io/contents/x", args[^1]);
    }

    [Fact]
    public void Gallery_dl_gets_current_gofile_salt()
    {
        var args = new List<string>();
        GalleryDlService.AddHostOptions(args, "https://gofile.io/d/Soimwa");
        Assert.Contains("extractor.gofile.salt=12af056dacea0b", args);
    }

    [Fact]
    public void Gallery_dump_reads_gofile_link_field()
    {
        const string json = """{"category":"gofile","filename":"clip","extension":"mp4","link":"https://store5.gofile.io/download/web/a/clip.mp4"}""";
        var items = GalleryDlParser.ParseItems(json);
        Assert.Single(items);
        Assert.Equal("https://store5.gofile.io/download/web/a/clip.mp4", items[0].Media.DownloadUrl);
        Assert.Equal(MediaKind.Video, items[0].Media.Kind);
    }

    [Fact]
    public void Parses_folder_json_files()
    {
        const string json = """
            {
              "name": "clips",
              "code": "Soimwa",
              "children": {
                "a": {
                  "type": "file",
                  "name": "one.jpg",
                  "mimetype": "image/jpeg",
                  "link": "https://store-eu-par-5.gofile.io/download/web/a/one.jpg"
                },
                "b": {
                  "type": "file",
                  "name": "two.mp4",
                  "mimetype": "video/mp4",
                  "link": "https://store-eu-par-5.gofile.io/download/web/b/two.mp4"
                },
                "c": { "type": "folder", "name": "nested", "id": "x" }
              }
            }
            """;
        var probe = GofileService.ParseFolder(JsonDocument.Parse(json).RootElement);
        Assert.Equal("GoFile", probe.Site);
        Assert.Equal("clips", probe.Title);
        Assert.Equal(EngineKind.GalleryDl, probe.Engine);
        Assert.Equal(1, probe.ImageCount);
        Assert.Equal(1, probe.VideoCount);
        Assert.Equal(2, probe.Items.Count);
        Assert.Equal("https://store-eu-par-5.gofile.io/download/web/a/one.jpg", probe.Items[0].DownloadUrl);
        Assert.Equal(MediaKind.Video, probe.Items[1].Kind);
    }

    [Theory]
    [InlineData("error-passwordRequired", "That folder needs a password.")]
    [InlineData("error-notFound", "That GoFile folder is gone.")]
    [InlineData("error-notPremium", "GoFile blocked that folder.")]
    [InlineData("error-rateLimit", "GoFile is busy. Try again in a minute.")]
    public void Maps_gofile_status_to_a_short_error(string status, string message)
        => Assert.Equal(message, GofileService.MessageForStatus(status));

    [Fact]
    public void Garbage_api_body_is_not_json()
    {
        Assert.False(GofileService.TryReadData("gzip", out _, out var error));
        Assert.Null(error);
        Assert.False(GofileService.TryReadData("{nope}", out _, out var bad));
        Assert.Equal("Could not read that link.", bad);
    }

    [Fact]
    public void Rewrites_http_gofile_links()
    {
        Assert.Equal(
            "https://store-eu-par-5.gofile.io/download/web/a/one.jpg",
            GofileService.HttpsLink("//store-eu-par-5.gofile.io/download/web/a/one.jpg"));
        Assert.Equal(
            "https://store5.gofile.io/download/web/b/two.mp4",
            GofileService.HttpsLink("http://store5.gofile.io/download/web/b/two.mp4"));
        Assert.Equal(
            "https://store5.gofile.io/a.mp4?token=abc",
            GofileService.WithAccountToken("https://store5.gofile.io/a.mp4", "abc"));
    }

    [Fact]
    public void Curl_args_force_http11_ipv4_and_resume()
    {
        var args = GofileCdn.CurlArgs("https://store5.gofile.io/a.mp4", @"C:\tmp\a.mp4.part", "tok");
        Assert.Contains("--http1.1", args);
        Assert.Contains("-4", args);
        Assert.Contains("--ssl-no-revoke", args);
        Assert.Contains("-C", args);
        Assert.Contains("Authorization: Bearer tok", args);
        Assert.Contains("accountToken=tok", args);
        Assert.Equal("https://store5.gofile.io/a.mp4", args[^1]);
    }

    [Fact]
    public void Resumes_existing_part_file()
    {
        var part = Path.Combine(Path.GetTempPath(), "WasdFetchIt-tests", Guid.NewGuid().ToString("N") + ".part");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        File.WriteAllBytes(part, new byte[12]);
        try
        {
            Assert.True(GofileCdn.ShouldResume(part, out var have));
            Assert.Equal(12, have);
        }
        finally
        {
            File.Delete(part);
        }
    }

    [Fact]
    public void Ssl_errors_map_to_a_retry_line()
    {
        var nested = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid."));
        Assert.True(GofileService.LooksLikeSsl(nested));
        Assert.Equal(
            "GoFile dropped the connection. Try Download again.",
            GofileService.SslMessage);
        Assert.Equal(
            "GoFile is blocked on this network. Try a VPN.",
            GofileService.UnreachableMessage);
    }

    [Fact]
    public void Partial_gofile_save_does_not_fall_through_to_gallery()
    {
        Assert.True(MediaFetcher.IsFinalGofileError(
            new InvalidOperationException("Saved 3 of 10 files.")));
        Assert.True(MediaFetcher.IsPartialSaveMessage("Saved 3 of 10 files."));
        Assert.False(MediaFetcher.IsFinalGofileError(
            new InvalidOperationException("Could not save those files.")));
        Assert.True(MediaFetcher.IsFinalGofileError(
            new InvalidOperationException(GofileService.UnreachableMessage)));
    }
}
