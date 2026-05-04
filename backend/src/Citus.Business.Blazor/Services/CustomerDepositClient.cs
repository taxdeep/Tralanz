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
