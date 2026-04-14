using Citus.Ui.Shared.Health;

namespace Citus.Business.Blazor.Services;

public sealed class AccountingHealthClient(HttpClient httpClient, ILogger<AccountingHealthClient> logger)
{
    public async Task<ServiceHealthStatus?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<ServiceHealthStatus>("health", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load Accounting API health for the business shell.");
            return null;
        }
    }
}
