using System.Net.Http;
using FetchIt.Services;

namespace FetchIt.Tests;

public class MediaFetcherTests
{
    [Fact]
    public void Partial_gofile_save_does_not_fallback_to_gallery()
    {
        var ex = new InvalidOperationException("Saved 3 of 5 files.");
        Assert.True(MediaFetcher.IsPartialSave(ex.Message));
        Assert.False(MediaFetcher.ShouldFallbackFromGofile(ex));
    }

    [Fact]
    public void Total_gofile_failure_still_falls_back()
    {
        Assert.True(MediaFetcher.ShouldFallbackFromGofile(
            new InvalidOperationException("Could not save those files.")));
        Assert.True(MediaFetcher.ShouldFallbackFromGofile(new HttpRequestException("HTTP 403")));
    }

    [Theory]
    [InlineData("That folder needs a password.")]
    [InlineData("Windows blocked that folder. Pick another save folder.")]
    [InlineData("Not a GoFile link.")]
    public void Known_gofile_errors_do_not_fallback(string message)
        => Assert.False(MediaFetcher.ShouldFallbackFromGofile(new InvalidOperationException(message)));

    [Fact]
    public void Cancel_and_ssl_do_not_fallback()
    {
        Assert.False(MediaFetcher.ShouldFallbackFromGofile(new OperationCanceledException()));
        Assert.False(MediaFetcher.ShouldFallbackFromGofile(
            new InvalidOperationException(GofileService.SslMessage)));
        Assert.False(MediaFetcher.ShouldFallbackFromGofile(
            new InvalidOperationException(GofileService.UnreachableMessage)));
    }
}
