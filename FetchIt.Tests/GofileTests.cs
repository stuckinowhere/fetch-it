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
    public void Maps_gofile_status_to_a_short_error(string status, string message)
        => Assert.Equal(message, GofileService.MessageForStatus(status));
}
