using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// M6 iter 4: HTTP client for the Drop-ship Clearing aging workbench.
/// Two endpoints — list aging rows, and write off a per-item residual.
/// Failures degrade gracefully on the read path (empty list) and surface
/// a user-displayable message on the write path so the workbench can
/// keep running across transient errors.
/// </summary>
public sealed class DropShipClearingClient(HttpClient httpClient, ILogger<DropShipClearingClient> logger)
{
    public async Task<IReadOnlyList<DropShipClearingAgingRowDto>> ListAgingAsync(
        bool hideBalanced,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = hideBalanced
                ? "accounting/inventory/drop-ship-clearing/aging?hideBalanced=true"
                : "accounting/inventory/drop-ship-clearing/aging";
            var rows = await httpClient.GetFromJsonAsync<DropShipClearingAgingRowDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<DropShipClearingAgingRowDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read drop-ship clearing aging.");
            return Array.Empty<DropShipClearingAgingRowDto>();
        }
    }

    public async Task<DropShipClearingWriteOffOutcome> WriteOffAsync(
        Guid itemId,
        DropShipClearingWriteOffRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/inventory/drop-ship-clearing/{itemId:D}/write-off",
                request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DropShipClearingWriteOffOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<DropShipClearingWriteOffResult>(cancellationToken);
            return new DropShipClearingWriteOffOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to post drop-ship clearing write-off for item {ItemId}.", itemId);
            return new DropShipClearingWriteOffOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record DropShipClearingAgingRowDto(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    decimal TotalBilledBase,
    decimal TotalQuantityBilled,
    decimal TotalInvoicedCogsBase,
    decimal TotalQuantityInvoiced,
    decimal NetClearingBase,
    DateOnly? OldestActivityDate,
    DateOnly? LatestActivityDate);

public sealed record DropShipClearingWriteOffRequest(
    decimal ExpectedNetClearingBase,
    string? Memo,
    string? IdempotencyKey = null);

public sealed record DropShipClearingWriteOffResult(
    Guid ItemId,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    decimal NetClearingAmountBase);

public sealed record DropShipClearingWriteOffOutcome(
    bool Succeeded,
    DropShipClearingWriteOffResult? Saved,
    string? ErrorMessage);
