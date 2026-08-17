using SeattleCarsInBikeLanes.Mobile.Core.Authentication;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class CookieHeaderParserTests
{
    [Fact]
    public void ParsesCookiePairsAndPreservesEqualsInValues()
    {
        IReadOnlyList<KeyValuePair<string, string>> cookies =
            CookieHeaderParser.Parse("session=abc==; theme=dark");

        Assert.Equal(
            [
                new KeyValuePair<string, string>("session", "abc=="),
                new KeyValuePair<string, string>("theme", "dark")
            ],
            cookies);
    }

    [Fact]
    public void DoesNotSplitQuotedSemicolon()
    {
        IReadOnlyList<KeyValuePair<string, string>> cookies =
            CookieHeaderParser.Parse("one=\"a;b\"; two=2");

        Assert.Equal("\"a;b\"", cookies[0].Value);
        Assert.Equal("two", cookies[1].Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ; malformed")]
    public void IgnoresMissingOrMalformedPairs(string? header)
    {
        Assert.Empty(CookieHeaderParser.Parse(header));
    }
}
