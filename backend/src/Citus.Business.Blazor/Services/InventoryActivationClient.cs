using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the Inventory Module activation wizard
/// (<c>POST /accounting/inventory/activate</c> +
/// <c>GET /accounting/inventory/activation-state</c>). Active company id
/// flows server-side via the X-Citus-Business-Active-Company-Id header
/// attached by BusinessSessionHeaderHandler — callers don't pass it.
/// </summary>
public sealed class InventoryActivationClient(HttpClient httpClient, ILogger<InventoryActivationClient> logger)
{
    public async Task<InventoryActivationStateSummary?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("accounting/inventory/activation-state", cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<InventoryActivationStateSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read inventory activation state.");
            return null;
        }
    }

    public async Task<InventoryActivationOutcome> ActivateAsync(
        InventoryActivationPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "accounting/inventory/activate",
                payload,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new InventoryActivationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<InventoryActivationResponse>(cancellationToken);
            return new InventoryActivationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to activate inventory module.");
            return new InventoryActivationOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record InventoryActivationPayload(
    string? ProfileTag,
    string? CostingMethod,
    string? WarehouseName);

public sealed record InventoryActivationResponse(
    bool ModuleEnabled,
    DateTimeOffset? EnabledAt,
    DateTimeOffset? LockedAt,
    string? ProfileTag,
    string DefaultCostingMethod,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    int CoaAccountsCreated,
    int CoaAccountsAlreadyPresent);

public sealed record InventoryActivationStateSummary(
    bool ModuleEnabled,
    DateTimeOffset? EnabledAt,
    DateTimeOffset? LockedAt,
    string? ProfileTag);

public sealed record InventoryActivationOutcome(
    bool Succeeded,
    InventoryActivationResponse? Saved,
    string? ErrorMessage);
