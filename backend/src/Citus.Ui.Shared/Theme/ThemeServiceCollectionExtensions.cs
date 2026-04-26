using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Citus.Ui.Shared.Theme;

public static class ThemeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Citus theme stack: a per-circuit <see cref="IThemeService"/>
    /// plus a per-request <see cref="ThemeCookieReader"/> for SSR pre-hydration.
    /// </summary>
    public static IServiceCollection AddCitusTheme(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddScoped<ThemeCookieReader>();
        services.TryAddScoped<IThemeService, ThemeService>();
        return services;
    }
}
