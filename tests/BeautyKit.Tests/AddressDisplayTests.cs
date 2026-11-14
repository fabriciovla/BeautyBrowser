using Xunit;

namespace BeautyKit.Tests;

public class AddressDisplayTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", "youtube.com/watch?v=abc")]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://example.com", "example.com")]
    [InlineData("http://example.com/a/b/", "example.com/a/b/")]
    [InlineData("https://example.com/?q=1", "example.com/?q=1")]
    [InlineData("https://example.com:8080/x#top", "example.com:8080/x#top")]
    [InlineData("https://es.wikipedia.org/wiki/Espa%C3%B1a", "es.wikipedia.org/wiki/España")]
    [InlineData("https://example.com/a%20b", "example.com/a%20b")]
    [InlineData("https://WWW.Example.com/x", "Example.com/x")]
    [InlineData("https://wwwexample.com/x", "wwwexample.com/x")]
    public void ShortAddressKeepsDomainAndPath(string url, string expected) => Assert.Equal(expected, AddressDisplay.ToShort(url));

    [Theory]
    [InlineData("beauty://newtab")]
    [InlineData("file:///C:/a.html")]
    [InlineData("about:blank")]
    [InlineData("")]
    public void ShortAddressLeavesOtherSchemesAlone(string url) => Assert.Equal(url, AddressDisplay.ToShort(url));
}
