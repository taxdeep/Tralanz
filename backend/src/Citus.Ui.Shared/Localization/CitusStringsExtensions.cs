using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Citus.Ui.Shared.Localization;

public static class CitusLocalizationExtensions
{
    public const string DefaultCulture = "en";

    public static readonly IReadOnlyList<string> SupportedCultures = new[] { "en", "zh-Hans" };

    /// <summary>
    /// Registers IStringLocalizer&lt;CitusStrings&gt; against the
    /// standard resource provider. Call this from each Blazor host's
    /// Program.cs after AddRazorComponents().
    /// </summary>
    public static IServiceCollection AddCitusLocalization(this IServiceCollection services)
    {
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        return services;
    }

    /// <summary>
    /// Wires up RequestLocalization with the en + zh-Hans culture
    /// list. Cookie-based culture switching (citus-culture cookie) is
    /// recommended; the helper accepts the host's preferred provider
    /// strategy via the configure callback.
    /// </summary>
    public static IApplicationBuilder UseCitusLocalization(this IApplicationBuilder app)
    {
        var supported = SupportedCultures.Select(c => new CultureInfo(c)).ToArray();
        app.UseRequestLocalization(options =>
        {
            options.DefaultRequestCulture = new(DefaultCulture);
            options.SupportedCultures = supported;
            options.SupportedUICultures = supported;
        });
        return app;
    }
}
