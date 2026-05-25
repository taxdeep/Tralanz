using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company Unit-of-Measure (UOM) master.
/// Backs the Item edit form's UOM dropdown + drives the qty input step
/// on Task / Invoice / Bill line grids. Failures degrade to an empty
/// list so an unreachable API doesn't block form rendering — the
/// downstream UI just shows the legacy free-input behaviour.
/// </summary>
public sealed class UomClient(HttpClient httpClient, ILogger<UomClient> logger)
{
    public async Task<IReadOnlyList<UomSummary>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = includeInactive ? "accounting/uom?includeInactive=true" : "accounting/uom";
            var rows = await httpClient.GetFromJsonAsync<UomSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<UomSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read units of measure.");
            return Array.Empty<UomSummary>();
        }
    }
}

/// <summary>
/// One row from GET /accounting/uom. Matches UomHttpSummary on the API.
/// </summary>
public sealed record UomSummary(
    Guid Id,
    CompanyId CompanyId,
    string Code,
    string Name,
    int DecimalPrecision,
    string? Category,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
