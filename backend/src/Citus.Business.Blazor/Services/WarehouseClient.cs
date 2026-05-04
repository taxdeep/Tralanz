using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company warehouses surface
/// (<c>/accounting/warehouses</c>). V1 inventory tier is single-warehouse;
/// the list call usually returns one row.
/// </summary>
public sealed class WarehouseClient(HttpClient httpClient, ILogger<WarehouseClient> logger)
{
    public async Task<IReadOnlyList<WarehouseSummary>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = includeInactive ? "accounting/warehouses?includeInactive=true" : "accounting/warehouses";
            var rows = await httpClient.GetFromJsonAsync<WarehouseSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<WarehouseSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read warehouses.");
            return Array.Empty<WarehouseSummary>();
        }
    }

    public async Task<WarehouseMutationOutcome> RenameAsync(
        Guid warehouseId,
        WarehouseRenamePayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsJsonAsync(
                $"accounting/warehouses/{warehouseId:D}",
                payload,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new WarehouseMutationOutcome(false, await ReadMessageAsync(response, cancellationToken));
            }
            return new WarehouseMutationOutcome(true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to rename warehouse {WarehouseId}.", warehouseId);
            return new WarehouseMutationOutcome(false, "Unable to reach the server. Please try again.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return $"Request failed with status code {(int)response.StatusCode}.";
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString() ?? raw;
            }
        }
        catch (JsonException) { }
        return raw;
    }
}

public sealed record WarehouseSummary(
    Guid Id,
    Guid CompanyId,
    string WarehouseCode,
    string Name,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WarehouseRenamePayload(
    string? WarehouseCode,
    string Name,
    string? Description);

public sealed record WarehouseMutationOutcome(
    bool Succeeded,
    string? ErrorMessage);
