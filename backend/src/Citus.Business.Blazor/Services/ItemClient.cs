using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the per-company inventory items (Products & Services).
/// Mirrors the <c>/accounting/items</c> surface served by the Accounting
/// API. Failures degrade gracefully: list returns empty, mutations carry
/// a user-displayable message in the outcome.
/// </summary>
public sealed class ItemClient(HttpClient httpClient, ILogger<ItemClient> logger)
{
    public async Task<IReadOnlyList<ItemSummary>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = includeInactive ? "accounting/items?includeInactive=true" : "accounting/items";
            var rows = await httpClient.GetFromJsonAsync<ItemSummary[]>(url, cancellationToken);
            return rows ?? Array.Empty<ItemSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read items.");
            return Array.Empty<ItemSummary>();
        }
    }

    public async Task<ItemMutationOutcome> CreateAsync(
        ItemUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendUpsertAsync(HttpMethod.Post, "accounting/items", payload, cancellationToken);

    public async Task<ItemMutationOutcome> UpdateAsync(
        Guid id,
        ItemUpsertPayload payload,
        CancellationToken cancellationToken = default)
        => await SendUpsertAsync(HttpMethod.Put, $"accounting/items/{id:D}", payload, cancellationToken);

    public async Task<ItemMutationOutcome> SetActiveAsync(
        Guid id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var path = isActive ? $"accounting/items/{id:D}/activate" : $"accounting/items/{id:D}/deactivate";
        try
        {
            using var response = await httpClient.PostAsync(path, content: null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ItemMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            return new ItemMutationOutcome(true, null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to toggle item active flag.");
            return new ItemMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<ItemMutationOutcome> SendUpsertAsync(
        HttpMethod method,
        string path,
        ItemUpsertPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ItemMutationOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<ItemSummary>(cancellationToken);
            return new ItemMutationOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to upsert item.");
            return new ItemMutationOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return $"Request failed with status code {(int)response.StatusCode}.";
        }
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? raw;
            }
        }
        catch (JsonException) { }
        return raw;
    }
}

public sealed record ItemSummary(
    Guid Id,
    CompanyId CompanyId,
    string ItemCode,
    string Name,
    string? Description,
    string ItemKind,
    string? StockUomCode,
    string ManageInventoryMethod,
    string DefaultCostingMethod,
    string BackorderMode,
    string LowStockActivity,
    Guid? DefaultInventoryAssetAccountId,
    Guid? DefaultCogsAccountId,
    Guid? DefaultWriteOffAccountId,
    Guid? DefaultPurchaseVarianceAccountId,
    Guid? DefaultSalesRevenueAccountId,
    Guid? DefaultDropShipClearingAccountId,
    decimal? DefaultSalesPrice,
    decimal? DefaultPurchasePrice,
    Guid? DefaultSalesTaxCodeId,
    Guid? DefaultPurchaseTaxCodeId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ItemUpsertPayload(
    string ItemCode,
    string Name,
    string? Description,
    string ItemKind,
    string? StockUomCode,
    string? ManageInventoryMethod,
    string? DefaultCostingMethod,
    string? BackorderMode,
    string? LowStockActivity,
    Guid? DefaultInventoryAssetAccountId,
    Guid? DefaultCogsAccountId,
    Guid? DefaultWriteOffAccountId,
    Guid? DefaultPurchaseVarianceAccountId,
    Guid? DefaultSalesRevenueAccountId,
    Guid? DefaultDropShipClearingAccountId,
    decimal? DefaultSalesPrice,
    decimal? DefaultPurchasePrice,
    Guid? DefaultSalesTaxCodeId,
    Guid? DefaultPurchaseTaxCodeId);

public sealed record ItemMutationOutcome(
    bool Succeeded,
    ItemSummary? Saved,
    string? ErrorMessage);
