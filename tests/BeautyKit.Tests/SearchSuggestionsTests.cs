using Xunit;

namespace BeautyKit.Tests;

public class SearchSuggestionsTests
{
    [Theory]
    [InlineData("Google", "suggestqueries.google.com")]
    [InlineData("DuckDuckGo", "duckduckgo.com/ac/")]
    [InlineData("Bing", "api.bing.com/qsonhs.aspx")]
    [InlineData("Ecosia", "ac.ecosia.org/autocomplete")]
    [InlineData("ecosia", "ac.ecosia.org/autocomplete")]
    [InlineData("Unknown", "suggestqueries.google.com")]
    public void EachEngineAsksItsOwnEndpoint(string engine, string expected) =>
        Assert.Contains(expected, SearchSuggestions.Url(engine, "hola", "es"));

    [Fact]
    public void TheQueryIsEscaped() =>
        Assert.EndsWith("q=a%20%26%20b", SearchSuggestions.Url("DuckDuckGo", "a & b", "en"));

    [Fact]
    public void ReadsTheQueryAndListFormat() =>
        Assert.Equal(["hola", "holanda"], SearchSuggestions.Parse("""["hola",["hola","holanda"],[],{"x":1}]"""));

    [Fact]
    public void ReadsBingsFormat() =>
        Assert.Equal(["hola", "holafly"], SearchSuggestions.Parse("""{"AS":{"Query":"hola","Results":[{"Type":"AS","Suggests":[{"Txt":"hola"},{"Txt":"holafly"}]}]}}"""));

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("{}")]
    [InlineData("[\"q\"]")]
    public void AnythingElseGivesNothing(string json) => Assert.Empty(SearchSuggestions.Parse(json));
}
