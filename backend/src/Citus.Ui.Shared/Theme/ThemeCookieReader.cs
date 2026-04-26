using Microsoft.AspNetCore.Http;

namespace Citus.Ui.Shared.Theme;

/// <summary>
/// Reads the <c>citus-theme</c> cookie during the static SSR pass so the
/// initial HTML can render with the right <c>&lt;html class&gt;</c> and
/// avoid a light → dark flash. Scoped per request.
/// </summary>
public sealed class ThemeCookieReader
{
    public const string CookieName = "citus-theme";

    public ThemeCookieReader(IHttpContextAccessor accessor)
    {
        var raw = accessor.HttpContext?.Request.Cookies[CookieName];
        Mode = ParseMode(raw);
        IsDark = Mode == ThemeMode.Dark || (Mode == ThemeMode.System && IsSystemDark(accessor.HttpContext));
    }

    public ThemeMode Mode { get; }

    public bool IsDark { get; }

    /// <summary>The class to put on the &lt;html&gt; element during SSR.</summary>
    public string HtmlClass => Mode switch
    {
        ThemeMode.Dark => "dark",
        ThemeMode.Light => "light",
        _ => IsDark ? "dark" : "light"
    };

    private static ThemeMode ParseMode(string? raw) => raw switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => ThemeMode.System
    };

    private static bool IsSystemDark(HttpContext? context)
    {
        // Honour Sec-CH-Prefers-Color-Scheme client hint when the browser sends it,
        // otherwise default to light. Citus sites that want dark by default can
        // ship a tiny inline script that sets the cookie before SSR; for now we
        // stay deterministic on the server.
        var hint = context?.Request.Headers["Sec-CH-Prefers-Color-Scheme"].ToString();
        return string.Equals(hint, "dark", StringComparison.OrdinalIgnoreCase);
    }
}
