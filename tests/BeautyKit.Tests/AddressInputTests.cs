using BeautyKit;
using BeautyKit.Blocking;
using Xunit;

namespace BeautyKit.Tests;

public class AddressInputTests
{
    private const string Template = "https://www.google.com/search?q={0}";

    // A tiny Public Suffix List: enough to tell real TLDs from file extensions.
    private static readonly PublicSuffix Psl = PublicSuffix.Parse("com\nar\ncom.ar\nuk\nco.uk\napp\nio\ngithub.io\n");

    private static string Url(string input, PublicSuffix? psl = null) =>
        AddressInput.ToUrl(input, Template, psl ?? Psl, "beauty");

    private static string Search(string text) => string.Format(Template, Uri.EscapeDataString(text));

    [Theory]
    [InlineData("192.168.1.1", "http://192.168.1.1/")]
    [InlineData("10.0.0.1:8080", "http://10.0.0.1:8080/")]
    [InlineData("192.168.0.1/admin", "http://192.168.0.1/admin")]
    [InlineData("[::1]", "http://[::1]/")]
    [InlineData("[::1]:3000/x", "http://[::1]:3000/x")]
    [InlineData("::1", "http://[::1]/")]
    public void IpAddressesGoOverHttp(string input, string expected) => Assert.Equal(expected, Url(input));

    [Theory]
    [InlineData("server:8080", "http://server:8080/")]
    [InlineData("miserver:3000/path", "http://miserver:3000/path")]
    [InlineData("example.com:8443", "http://example.com:8443/")]
    [InlineData("node.js:3000", "http://node.js:3000/")]
    [InlineData("localhost:5173", "http://localhost:5173")]
    [InlineData("localhost", "http://localhost")]
    public void HostWithPortGoesOverHttp(string input, string expected) => Assert.Equal(expected, Url(input));

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("www.clarin.com.ar/a?b=1", "https://www.clarin.com.ar/a?b=1")]
    [InlineData("EXAMPLE.COM", "https://example.com/")]
    [InlineData("miapp.vercel.app", "https://miapp.vercel.app/")]
    [InlineData("foo.github.io", "https://foo.github.io/")]
    [InlineData("user@example.com/x", "https://user@example.com/x")]
    public void DomainsWithARealTldGoOverHttps(string input, string expected) => Assert.Equal(expected, Url(input));

    [Theory]
    [InlineData("node.js")]
    [InlineData("archivo.txt")]
    [InlineData("foo.exe")]
    [InlineData("v1.2.3")]
    [InlineData("3.14")]
    [InlineData("e.g.")]
    [InlineData("hola")]
    [InlineData("hola mundo.com")]
    [InlineData("a.b.")]
    [InlineData("foo.com:")]
    [InlineData("foo.com:99999")]
    [InlineData("256.1.1.1")]
    [InlineData("")]
    public void EverythingElseIsASearch(string input) => Assert.Equal(Search(input), Url(input));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(document.cookie)")]
    [InlineData("javascript:1")]
    [InlineData("javascript://x.com/%0aalert(1)")]
    public void JavascriptIsSearchedNotLoaded(string input) => Assert.Equal(Search(input), Url(input));

    [Theory]
    [InlineData("https://example.com/a%20b/c%2Fd", "https://example.com/a%20b/c%2Fd")]
    [InlineData("http://example.com/?q=a%26b", "http://example.com/?q=a%26b")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("chrome://settings", "chrome://settings/")]
    [InlineData("beauty://newtab", "beauty://newtab/")]
    public void ExplicitSchemesKeepTheirEscaping(string input, string expected) => Assert.Equal(expected, Url(input));

    [Fact]
    public void DataUrlsNavigate() =>
        Assert.StartsWith("data:text/html", Url("data:text/html,<h1>hola</h1>"));

    [Fact]
    public void TldsAreCheckedAgainstTheFallbackWhenTheListIsMissing()
    {
        Assert.Equal("https://example.com/", Url("example.com", PublicSuffix.Fallback));
        Assert.Equal("https://clarin.com.ar/", Url("clarin.com.ar", PublicSuffix.Fallback));
        Assert.Equal(Search("node.js"), Url("node.js", PublicSuffix.Fallback));
        Assert.Equal(Search("archivo.txt"), Url("archivo.txt", PublicSuffix.Fallback));
    }

    [Fact]
    public void SearchEscapesTheText() => Assert.Equal(Search("c# tutorial"), Url("c# tutorial"));
}

public class PublicSuffixTests
{
    [Fact]
    public void DifferentSubdomainsOfAPrivateSuffixAreDifferentSites()
    {
        var psl = PublicSuffix.Parse("com\napp\nvercel.app\ngithub.io\n");
        Assert.Equal("a.vercel.app", psl.Domain("a.vercel.app"));
        Assert.NotEqual(psl.Domain("a.vercel.app"), psl.Domain("b.vercel.app"));
        Assert.Equal("foo.github.io", psl.Domain("x.foo.github.io"));
        Assert.Equal("google.com", psl.Domain("www.google.com"));
    }
}
