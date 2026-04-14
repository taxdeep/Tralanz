using Citus.Ui.Shared.Business;

namespace Web.Shell.Services;

public sealed class WebShellBusinessSessionClient(HttpClient httpClient, ILogger<WebShellBusinessSessionClient> logger)
{
    public async Task<BusinessSessionContextSummary?> GetContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<BusinessSessionContextSummary>(
                "accounting/session/context",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the Web.Shell business session context probe.");
            return null;
        }
    }
}
