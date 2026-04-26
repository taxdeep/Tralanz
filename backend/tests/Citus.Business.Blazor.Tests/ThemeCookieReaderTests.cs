using Citus.Ui.Shared.Theme;
using Microsoft.AspNetCore.Http;

namespace Citus.Business.Blazor.Tests;

public sealed class ThemeCookieReaderTests
{
    [Theory]
    [InlineData("dark", ThemeMode.Dark, true, "dark")]
    [InlineData("light", ThemeMode.Light, false, "light")]
    [InlineData("anything-else", ThemeMode.System, false, "light")]
    [InlineData(null, ThemeMode.System, false, "light")]
    public void ParsesCookieAndExposesHtmlClass(string? cookieValue, ThemeMode expectedMode, bool expectedDark, string expectedClass)
    {
        var accessor = CreateAccessor(cookieValue, prefersDarkHint: false);

        var reader = new ThemeCookieReader(accessor);

        Assert.Equal(expectedMode, reader.Mode);
        Assert.Equal(expectedDark, reader.IsDark);
        Assert.Equal(expectedClass, reader.HtmlClass);
    }

    [Fact]
    public void HonoursPrefersColorSchemeHintOnSystemMode()
    {
        var accessor = CreateAccessor(cookieValue: null, prefersDarkHint: true);

        var reader = new ThemeCookieReader(accessor);

        Assert.Equal(ThemeMode.System, reader.Mode);
        Assert.True(reader.IsDark);
        Assert.Equal("dark", reader.HtmlClass);
    }

    private static IHttpContextAccessor CreateAccessor(string? cookieValue, bool prefersDarkHint)
    {
        var context = new DefaultHttpContext();
        if (cookieValue is not null)
        {
            context.Request.Headers.Cookie = $"{ThemeCookieReader.CookieName}={cookieValue}";
        }
        if (prefersDarkHint)
        {
            context.Request.Headers["Sec-CH-Prefers-Color-Scheme"] = "dark";
        }

        return new HttpContextAccessor { HttpContext = context };
    }
}
