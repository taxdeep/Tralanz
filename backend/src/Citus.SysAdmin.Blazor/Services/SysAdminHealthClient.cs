using Citus.Ui.Shared.Health;

namespace Citus.SysAdmin.Blazor.Services;

public sealed class SysAdminHealthClient(HttpClient httpClient, ILogger<SysAdminHealthClient> logger)
{
    public async Task<ServiceHealthStatus?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<ServiceHealthStatus>("health", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load SysAdmin API health.");
            return null;
        }
    }
}
