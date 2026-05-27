using Microsoft.Extensions.Options;

namespace Aiseworks.ApiClient;

internal sealed class AiseworksSystemHealthClient(
    HttpClient httpClient,
    IOptions<AiseworksApiClientOptions> options) : IAiseworksSystemHealthClient
{
    public async Task<SystemHealthSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        var configured = options.Value;
        var probes = await Task.WhenAll(
            ProbeAsync("Accounting API", BuildHealthUri(configured.AccountingApiBaseUrl), cancellationToken),
            ProbeAsync("Business UI", BuildHealthUri(configured.BusinessBaseUrl), cancellationToken),
            ProbeAsync("SysAdmin API", BuildHealthUri(configured.SysAdminApiBaseUrl), cancellationToken));

        var overall = probes.All(probe => probe.IsHealthy) ? "Online" : "Degraded";
        return new SystemHealthSnapshot(overall, DateTimeOffset.UtcNow, probes);
    }

    private async Task<ServiceHealthProbe> ProbeAsync(
        string name,
        Uri target,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(target, cancellationToken);
            return new ServiceHealthProbe(
                name,
                target,
                response.IsSuccessStatusCode ? "Online" : $"HTTP {(int)response.StatusCode}",
                response.IsSuccessStatusCode);
        }
        catch (TaskCanceledException)
        {
            return new ServiceHealthProbe(name, target, "Timeout", false);
        }
        catch (HttpRequestException)
        {
            return new ServiceHealthProbe(name, target, "Offline", false);
        }
    }

    private static Uri BuildHealthUri(Uri baseUri)
    {
        var text = baseUri.ToString();
        if (text.EndsWith("/health", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith("/system/health", StringComparison.OrdinalIgnoreCase))
        {
            return baseUri;
        }

        var path = baseUri.Port is 18080 or 18090
            ? "system/health"
            : "health";

        return new Uri(new Uri(text.TrimEnd('/') + "/", UriKind.Absolute), path);
    }
}
