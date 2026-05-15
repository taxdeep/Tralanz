using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharedKernel.Identity;

namespace Citus.Business.Blazor.Services;

public sealed class BankReconciliationClient(HttpClient httpClient, ILogger<BankReconciliationClient> logger)
{
    public async Task<BankReconciliationLedgerOutcome> ListLedgerEntriesAsync(
        Guid bankAccountId,
        DateOnly statementDate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/reconciliation/ledger?accountId={bankAccountId:D}&statementDate={statementDate:yyyy-MM-dd}";
            var response = await httpClient.GetFromJsonAsync<BankReconciliationLedgerResponse>(url, cancellationToken);
            return new BankReconciliationLedgerOutcome(true, response?.Entries ?? Array.Empty<BankReconciliationLedgerEntrySummary>(), null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read bank reconciliation ledger entries.");
            return new BankReconciliationLedgerOutcome(false, Array.Empty<BankReconciliationLedgerEntrySummary>(), "Unable to load unreconciled ledger entries.");
        }
    }

    public async Task<BankReconciliationCompleteOutcome> CompleteAsync(
        BankReconciliationCompletePayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "accounting/reconciliation/complete",
                payload,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new BankReconciliationCompleteOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }

            var summary = await response.Content.ReadFromJsonAsync<BankReconciliationSummaryDto>(cancellationToken);
            return new BankReconciliationCompleteOutcome(true, summary, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to complete bank reconciliation.");
            return new BankReconciliationCompleteOutcome(false, null, "Unable to reach the server. Please try again.");
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

public sealed record BankReconciliationLedgerOutcome(
    bool Succeeded,
    IReadOnlyList<BankReconciliationLedgerEntrySummary> Entries,
    string? ErrorMessage);

public sealed record BankReconciliationCompleteOutcome(
    bool Succeeded,
    BankReconciliationSummaryDto? Summary,
    string? ErrorMessage);

public sealed record BankReconciliationLedgerResponse(
    IReadOnlyList<BankReconciliationLedgerEntrySummary> Entries);

public sealed record BankReconciliationLedgerEntrySummary(
    Guid LedgerEntryId,
    Guid JournalEntryId,
    Guid JournalEntryLineId,
    DateOnly PostingDate,
    Guid AccountId,
    string AccountCode,
    string AccountName,
    string DisplayNumber,
    string SourceType,
    Guid SourceId,
    string TransactionCurrencyCode,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    decimal SignedAmountBase,
    decimal SignedAmountTransaction,
    string Description);

public sealed record BankReconciliationCompletePayload(
    Guid BankAccountId,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal EndingBalance,
    IReadOnlyList<Guid> LedgerEntryIds,
    string? Notes);

public sealed record BankReconciliationSummaryDto(
    Guid ReconciliationId,
    Guid BankAccountId,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal EndingBalance,
    decimal ClearedIncrease,
    decimal ClearedDecrease,
    decimal CalculatedEndingBalance,
    decimal Difference,
    int LineCount,
    UserId CompletedByUserId,
    DateTimeOffset CompletedAt);
