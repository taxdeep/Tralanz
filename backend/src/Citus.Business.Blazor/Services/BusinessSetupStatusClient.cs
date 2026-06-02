using System.Net.Http.Json;

namespace Citus.Business.Blazor.Services;

public sealed class BusinessSetupStatusClient(HttpClient httpClient, ILogger<BusinessSetupStatusClient> logger)
{
    public async Task<BusinessSetupStatus?> GetSetupStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("auth/setup", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Business setup status check returned non-success status code {StatusCode}.", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<BusinessSetupStatus>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read setup status from SysAdmin API.");
            return null;
        }
    }
}

public sealed class BusinessSetupStatus
{
    public bool SetupRequired { get; set; }

    public bool BusinessReady { get; set; }

    public bool FirstCompanySetupRequired { get; set; }

    public bool HasAnyAccount { get; set; }

    public bool HasAnyCompany { get; set; }

    public bool HasAnyOwnerMembership { get; set; }

    public string SetupStage { get; set; } = "uninitialized";
}
