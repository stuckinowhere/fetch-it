using FetchIt.Services;

namespace FetchIt.Tests;

public class AppLogTests
{
    [Theory]
    [InlineData("--cookies C:\\Temp\\fetchit-ig-abc.txt https://x", "--cookies *** https://x")]
    [InlineData("-o extractor.gofile.api-token=abc123", "-o extractor.gofile.api-token=***")]
    [InlineData("sessionid=abc123; csrftoken=zzz", "sessionid=***; csrftoken=zzz")]
    [InlineData("-H Authorization: Bearer abc123 -b accountToken=abc123", "-H Authorization: Bearer *** -b accountToken=***")]
    [InlineData("https://store1.gofile.io/download/web/X/f.bin?token=abc123&x=1", "https://store1.gofile.io/download/web/X/f.bin?token=***&x=1")]
    [InlineData("https://gofile.io/d/X?password=hunter2", "https://gofile.io/d/X?password=***")]
    [InlineData("fetch https://gofile.io/d/X#hunter2", "fetch https://gofile.io/d/X#***")]
    [InlineData("plain line with nothing secret", "plain line with nothing secret")]
    public void Redact_masks_every_credential_form_the_app_builds(string input, string expected)
        => Assert.Equal(expected, AppLog.Redact(input));

    [Theory]
    [InlineData("https://gofile.io/d/X#hunter2", "https://gofile.io/d/X")]
    [InlineData("https://gofile.io/d/X?password=hunter2&y=1", "https://gofile.io/d/X")]
    [InlineData("  https://www.youtube.com/watch?v=abc  ", "https://www.youtube.com/watch")]
    [InlineData("not a url #secret", "not a url #***")]
    [InlineData(null, "")]
    public void SafeUrl_drops_query_and_fragment(string? input, string expected)
        => Assert.Equal(expected, AppLog.SafeUrl(input));
}
