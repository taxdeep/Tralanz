using Citus.Ui.Shared.Business;

namespace Citus.Business.Blazor.Services;

public sealed class BusinessSessionClient(HttpClient httpClient, ILogger<BusinessSessionClient> logger)
{
    public async Task<BusinessSessionContextSummary?> GetContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<BusinessSessionContextSummary>("accounting/session/context", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the business session context.");
            return null;
        }
    }
}
