using Microsoft.Extensions.DependencyInjection;

namespace Aiseworks.ApiClient;

public static class AiseworksApiClientServiceCollectionExtensions
{
    public static IServiceCollection AddAiseworksApiClient(
        this IServiceCollection services,
        Action<AiseworksApiClientOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHttpClient<IAiseworksSystemHealthClient, AiseworksSystemHealthClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(4);
        });

        return services;
    }
}
