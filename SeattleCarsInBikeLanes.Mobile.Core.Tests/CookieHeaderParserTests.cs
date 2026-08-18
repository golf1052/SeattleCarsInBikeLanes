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
    public void PreservesAllValidCookiesIncludingChunkedAuthenticationCookies()
    {
        IReadOnlyList<KeyValuePair<string, string>> cookies =
            CookieHeaderParser.Parse(
                "theme=dark; bsky_sessionC1=first; bsky_sessionC2=second");

        Assert.Equal(
            [
                new KeyValuePair<string, string>("theme", "dark"),
                new KeyValuePair<string, string>("bsky_sessionC1", "first"),
                new KeyValuePair<string, string>("bsky_sessionC2", "second")
            ],
            cookies);
    }

    [Fact]
    public void SkipsMalformedCookieAndPreservesValidCookies()
    {
        IReadOnlyList<KeyValuePair<string, string>> cookies =
            CookieHeaderParser.Parse(
                """first=1; ipt={"v":{"L":3},"pt":{"d":3},ct":{},"_t":44,"_v":"2"}; last=2""");

        Assert.Equal(
            [
                new KeyValuePair<string, string>("first", "1"),
                new KeyValuePair<string, string>("last", "2")
            ],
            cookies);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" ; malformed")]
    public void IgnoresMissingOrMalformedPairs(string? header)
    {
        Assert.Empty(CookieHeaderParser.Parse(header));
    }
}
