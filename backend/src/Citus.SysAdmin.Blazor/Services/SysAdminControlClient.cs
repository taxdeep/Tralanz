using Citus.Ui.Shared.Control;
using Citus.Ui.Shared.Shell;

namespace Citus.SysAdmin.Blazor.Services;

public sealed class SysAdminControlClient(HttpClient httpClient, ILogger<SysAdminControlClient> logger)
{
    public async Task<SysAdminControlContextSummary?> GetContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<SysAdminControlContextSummary>("control/context", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load SysAdmin control context.");
            return null;
        }
    }

    public async Task<IReadOnlyList<CompanyWorkspaceSummary>> ListCompaniesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<IReadOnlyList<CompanyWorkspaceSummary>>("control/companies", cancellationToken) ??
                Array.Empty<CompanyWorkspaceSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load managed company workspaces.");
            return Array.Empty<CompanyWorkspaceSummary>();
        }
    }

    public async Task<IReadOnlyList<ManagedUserSummary>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<IReadOnlyList<ManagedUserSummary>>("control/users", cancellationToken) ??
                Array.Empty<ManagedUserSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load managed users.");
            return Array.Empty<ManagedUserSummary>();
        }
    }

    public async Task<SysAdminControlContextSummary?> SetActiveCompanyAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsync($"control/active-company/{companyId}", content: null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Active company switch returned non-success status code {StatusCode} for company {CompanyId}.",
                    response.StatusCode,
                    companyId);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SysAdminControlContextSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to switch active company to {CompanyId}.", companyId);
            return null;
        }
    }

    public async Task<MaintenanceStateSummary?> UpdateMaintenanceAsync(
        MaintenanceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsJsonAsync("control/maintenance", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Maintenance update returned non-success status code {StatusCode}.", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<MaintenanceStateSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to update maintenance state.");
            return null;
        }
    }
}
