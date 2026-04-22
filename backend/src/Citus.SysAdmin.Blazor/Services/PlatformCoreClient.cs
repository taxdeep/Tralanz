using Citus.Ui.Shared.Platform;
using Citus.Ui.Shared.Control;
using Citus.SysAdmin.Blazor.State;

namespace Citus.SysAdmin.Blazor.Services;

public sealed class PlatformCoreClient(
    HttpClient httpClient,
    AppShellState shellState,
    ILogger<PlatformCoreClient> logger)
{
    public async Task<PlatformBootstrapReportSummary?> BootstrapAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PostAsync("core/bootstrap", content: null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Platform bootstrap request returned non-success status code {StatusCode}.",
                    response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PlatformBootstrapReportSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to bootstrap the platform core from SysAdmin API.");
            return null;
        }
    }

    public async Task<PlatformOverviewSummary?> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<PlatformOverviewSummary>("core", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load platform overview from SysAdmin API.");
            return null;
        }
    }

    public async Task<IReadOnlyList<PlatformModuleSummary>> ListModulesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<IReadOnlyList<PlatformModuleSummary>>("core/modules", cancellationToken) ??
                Array.Empty<PlatformModuleSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load platform modules from SysAdmin API.");
            return Array.Empty<PlatformModuleSummary>();
        }
    }

    public async Task<IReadOnlyList<PlatformEntitySummary>> ListEntitiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<IReadOnlyList<PlatformEntitySummary>>("core/entities", cancellationToken) ??
                Array.Empty<PlatformEntitySummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load platform entities from SysAdmin API.");
            return Array.Empty<PlatformEntitySummary>();
        }
    }

    public async Task<PlatformEntityDetail?> GetEntityAsync(string entityName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            return null;
        }

        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<PlatformEntityDetail>(
                $"core/entities/{Uri.EscapeDataString(entityName.Trim())}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load platform entity detail for {EntityName}.", entityName);
            return null;
        }
    }

    private void ApplySessionHeader()
    {
        httpClient.DefaultRequestHeaders.Remove(SysAdminAuthConstants.SessionHeaderName);

        if (shellState.IsAuthenticated)
        {
            httpClient.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, shellState.SessionToken);
        }
    }
}
