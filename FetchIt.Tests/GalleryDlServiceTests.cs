using FetchIt.Services;

namespace FetchIt.Tests;

public class GalleryDlServiceTests
{
    [Theory]
    [InlineData(@"C:\Users\a\Downloads\post_1.jpg", true)]
    [InlineData(@"# C:\Users\a\Downloads\post_1.jpg", true)]
    [InlineData("instagram/user/123.jpg", true)]
    [InlineData("[instagram][info] Visiting https://www.instagram.com/p/abc/", false)]
    [InlineData("[instagram][error] HttpError: 401 Unauthorized for 'https://www.instagram.com/p/abc/'", false)]
    [InlineData(@"[download][error] Failed to write C:\Users\a\Downloads\post_1.jpg", false)]
    [InlineData("[instagram][warning] Rate limit for https://www.instagram.com/", false)]
    [InlineData("", false)]
    public void LooksLikeSavedFile_ignores_log_and_error_lines(string line, bool saved)
        => Assert.Equal(saved, GalleryDlService.LooksLikeSavedFile(line));

    [Theory]
    [InlineData(true, true, "HTTP redirect to login page (https://www.instagram.com/accounts/login/)", true)]
    [InlineData(true, true, "HttpError: 401 Unauthorized", true)]
    [InlineData(true, true, "Redirected to Challenge page", true)]
    [InlineData(true, true, "NotFoundError: post not found", false)]
    [InlineData(true, true, "", false)]
    [InlineData(true, false, "NotFoundError: post not found", true)]
    [InlineData(false, true, "HTTP redirect to login page", false)]
    public void Saved_session_survives_failures_that_are_not_about_login(
        bool social, bool hasSavedSession, string toolOutput, bool asksToSignIn)
    {
        var expected = asksToSignIn ? MediaRouter.InstagramSessionMessage : "Could not read that link.";
        Assert.Equal(expected, GalleryDlService.ReadError(social, hasSavedSession, toolOutput));
    }

    [Fact]
    public void EnsureToolSucceeded_exit_zero_is_ok()
        => GalleryDlService.EnsureToolSucceeded(0, done: 0, fileCount: 3);

    [Fact]
    public void EnsureToolSucceeded_nonzero_without_saves_fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GalleryDlService.EnsureToolSucceeded(1, done: 0, fileCount: 3));
        Assert.Equal("Could not save those files.", ex.Message);
    }

    [Fact]
    public void EnsureToolSucceeded_nonzero_after_partial_save_fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GalleryDlService.EnsureToolSucceeded(1, done: 2, fileCount: 5));
        Assert.Equal("Saved 2 of 5 files.", ex.Message);
        Assert.True(MediaFetcher.IsPartialSaveMessage(ex.Message));
    }
}
