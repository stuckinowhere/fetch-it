using FetchIt.Controls;

namespace FetchIt.Tests;

public class WebViewHostTests
{
    private static readonly string[] InstagramHosts = ["instagram.com", "instagr.am"];

    [Theory]
    [InlineData("https://www.instagram.com/accounts/login/", true)]
    [InlineData("https://instagram.com/", true)]
    [InlineData("https://i.instagram.com/", true)]
    [InlineData("https://www.instagr.am/", true)]
    [InlineData("about:blank", true)]
    [InlineData("http://www.instagram.com/", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("https://github.com/", false)]
    public void HostIsAllowed_instagram_only(string url, bool allowed)
        => Assert.Equal(allowed, WebView2Host.HostIsAllowed(url, InstagramHosts));
}
