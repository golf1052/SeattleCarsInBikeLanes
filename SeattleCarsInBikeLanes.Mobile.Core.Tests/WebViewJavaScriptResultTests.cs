using SeattleCarsInBikeLanes.Mobile.Core.Navigation;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class WebViewJavaScriptResultTests
{
    private const string Json = """{"token":"secret","endpoint":"https://example.social"}""";

    [Fact]
    public void LeavesPlainJsonUnchanged()
    {
        Assert.Equal(Json, WebViewJavaScriptResult.DecodeJson(Json));
    }

    [Fact]
    public void DecodesIosQuotedJson()
    {
        const string result =
            "\"{\\\"token\\\":\\\"secret\\\",\\\"endpoint\\\":\\\"https://example.social\\\"}\"";

        Assert.Equal(Json, WebViewJavaScriptResult.DecodeJson(result));
    }

    [Fact]
    public void DecodesAndroidJsonWithEscapedQuotes()
    {
        const string result =
            "{\\\"token\\\":\\\"secret\\\",\\\"endpoint\\\":\\\"https://example.social\\\"}";

        Assert.Equal(Json, WebViewJavaScriptResult.DecodeJson(result));
    }
}
