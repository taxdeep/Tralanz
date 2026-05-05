using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// M5 iter 3: HTTP client for posting standalone Customer Deposits
/// against an open / confirmed Sales Order. Wraps
/// <c>POST /accounting/sales-orders/{id}/deposit</c>; the server-side
/// handler resolves the customer + currency from the SO so the request
/// only carries the deposit-specific fields (bank account, amount,
/// date, memo).
/// </summary>
public sealed class CustomerDepositClient(HttpClient httpClient, ILogger<CustomerDepositClient> logger)
{
    public async Task<CustomerDepositPostOutcome> PostAsync(
        Guid salesOrderId,
        CustomerDepositPostRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/sales-orders/{salesOrderId:D}/deposit",
                request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new CustomerDepositPostOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var saved = await response.Content.ReadFromJsonAsync<CustomerDepositPostResult>(cancellationToken);
            return new CustomerDepositPostOutcome(true, saved, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to post customer deposit for SO {SalesOrderId}.", salesOrderId);
            return new CustomerDepositPostOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    /// <summary>
    /// M5 wrap-up: per-SO deposit balance + per-deposit detail. Returns
    /// an empty summary when the SO has no deposits — never throws on
    /// the empty-result case.
    /// </summary>
    public async Task<SalesOrderCustomerDepositSummaryDto> GetForSalesOrderAsync(
        Guid salesOrderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/sales-orders/{salesOrderId:D}/deposits";
            var summary = await httpClient.GetFromJsonAsync<SalesOrderCustomerDepositSummaryDto>(url, cancellationToken);
            return summary ?? SalesOrderCustomerDepositSummaryDto.Empty(salesOrderId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read deposit summary for SO {SalesOrderId}.", salesOrderId);
            return SalesOrderCustomerDepositSummaryDto.Empty(salesOrderId);
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

public sealed record CustomerDepositPostRequest(
    Guid DepositToAccountId,
    decimal AmountTx,
    DateOnly? DocumentDate,
    string? Memo,
    string? IdempotencyKey = null);

public sealed record CustomerDepositPostResult(
    Guid CustomerDepositId,
    string DisplayNumber,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    decimal AmountBase);

public sealed record CustomerDepositPostOutcome(
    bool Succeeded,
    CustomerDepositPostResult? Saved,
    string? ErrorMessage);

public sealed record SalesOrderCustomerDepositSummaryDto(
    Guid SalesOrderId,
    decimal TotalOriginalBase,
    decimal TotalAppliedBase,
    decimal TotalOpenBase,
    IReadOnlyList<CustomerDepositRowDto> Deposits)
{
    public static SalesOrderCustomerDepositSummaryDto Empty(Guid salesOrderId) =>
        new(salesOrderId, 0m, 0m, 0m, Array.Empty<CustomerDepositRowDto>());
}

public sealed record CustomerDepositRowDto(
    Guid Id,
    string DisplayNumber,
    DateOnly DepositDate,
    decimal OriginalAmountBase,
    decimal AppliedAmountBase,
    decimal OpenAmountBase,
    string Status,
    DateTimeOffset? PostedAt);
