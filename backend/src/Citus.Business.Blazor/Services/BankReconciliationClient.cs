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

    public async Task<BankRegisterOutcome> ListBankRegisterAsync(
        Guid accountId,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"accounting/bank-register/{accountId:D}?take={take}";
            var response = await httpClient.GetFromJsonAsync<BankRegisterResponse>(url, cancellationToken);
            return new BankRegisterOutcome(
                true,
                response?.Entries ?? Array.Empty<BankRegisterEntryDto>(),
                null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read bank register for account {AccountId}.", accountId);
            return new BankRegisterOutcome(
                false,
                Array.Empty<BankRegisterEntryDto>(),
                "Unable to load bank register entries.");
        }
    }

    // R-3/R-4: draft lifecycle + carry-forward + report. Each method
    // returns an Outcome<T> wrapper so the page can render an error
    // banner without throwing into the Blazor render pipeline.

    public async Task<DraftOutcome> OpenDraftAsync(
        BankReconciliationDraftOpenPayload payload,
        CancellationToken cancellationToken = default) =>
        await PostJsonAsync<BankReconciliationDraftDto>(
            "accounting/reconciliation/draft", payload, cancellationToken, "open draft");

    public async Task<DraftOutcome> FindOpenDraftForAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/reconciliation/draft?accountId={accountId:D}",
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return new DraftOutcome(true, null, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new DraftOutcome(false, null, await ReadMessageAsync(response, cancellationToken));
            }
            var draft = await response.Content.ReadFromJsonAsync<BankReconciliationDraftDto>(cancellationToken);
            return new DraftOutcome(true, draft, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to look up in-flight draft for account {AccountId}.", accountId);
            return new DraftOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<DraftOutcome> LoadDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var draft = await httpClient.GetFromJsonAsync<BankReconciliationDraftDto>(
                $"accounting/reconciliation/draft/{draftId:D}",
                cancellationToken);
            return new DraftOutcome(draft is not null, draft, draft is null ? "Draft not found." : null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load draft {DraftId}.", draftId);
            return new DraftOutcome(false, null, "Unable to load the reconciliation draft.");
        }
    }

    public async Task<DraftCandidatesOutcome> ListCandidatesAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<DraftCandidatesResponseDto>(
                $"accounting/reconciliation/draft/{draftId:D}/candidates",
                cancellationToken);
            return new DraftCandidatesOutcome(
                true,
                response?.Candidates ?? Array.Empty<BankReconciliationDraftCandidateDto>(),
                null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load candidates for draft {DraftId}.", draftId);
            return new DraftCandidatesOutcome(
                false,
                Array.Empty<BankReconciliationDraftCandidateDto>(),
                "Unable to load candidate ledger entries.");
        }
    }

    public async Task<DraftOutcome> ToggleClearedAsync(
        Guid draftId,
        Guid ledgerEntryId,
        bool cleared,
        CancellationToken cancellationToken = default) =>
        await SendJsonAsync<BankReconciliationDraftDto>(
            HttpMethod.Put,
            $"accounting/reconciliation/draft/{draftId:D}/cleared",
            new BankReconciliationDraftToggleDto(ledgerEntryId, cleared),
            cancellationToken,
            "toggle cleared");

    public async Task<DraftOutcome> PatchDraftAsync(
        Guid draftId,
        BankReconciliationDraftPatchPayload payload,
        CancellationToken cancellationToken = default) =>
        await SendJsonAsync<BankReconciliationDraftDto>(
            HttpMethod.Patch,
            $"accounting/reconciliation/draft/{draftId:D}",
            payload,
            cancellationToken,
            "edit info");

    public async Task<BankReconciliationActionOutcome> AbandonDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.DeleteAsync(
                $"accounting/reconciliation/draft/{draftId:D}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new BankReconciliationActionOutcome(false, await ReadMessageAsync(response, cancellationToken));
            }
            return new BankReconciliationActionOutcome(true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to abandon draft {DraftId}.", draftId);
            return new BankReconciliationActionOutcome(false, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<BankReconciliationCompleteOutcome> CompleteDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/reconciliation/draft/{draftId:D}/complete",
                new { },
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
            logger.LogWarning(ex, "Unable to complete draft {DraftId}.", draftId);
            return new BankReconciliationCompleteOutcome(false, null, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<BankReconciliationActionOutcome> UndoCompletedAsync(
        Guid reconciliationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/reconciliation/{reconciliationId:D}/undo",
                new { },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new BankReconciliationActionOutcome(false, await ReadMessageAsync(response, cancellationToken));
            }
            return new BankReconciliationActionOutcome(true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to undo reconciliation {Id}.", reconciliationId);
            return new BankReconciliationActionOutcome(false, "Unable to reach the server. Please try again.");
        }
    }

    public async Task<BankReconciliationLastCompletedDto?> GetLastCompletedAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"accounting/reconciliation/last-completed?accountId={accountId:D}",
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadFromJsonAsync<BankReconciliationLastCompletedDto>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to look up last completed reconciliation for account {AccountId}.", accountId);
            return null;
        }
    }

    public async Task<BankReconciliationReportDto?> LoadReportAsync(
        Guid reconciliationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<BankReconciliationReportDto>(
                $"accounting/reconciliation/{reconciliationId:D}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load reconciliation report {Id}.", reconciliationId);
            return null;
        }
    }

    private async Task<DraftOutcome> PostJsonAsync<T>(
        string url,
        object body,
        CancellationToken cancellationToken,
        string verb)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(url, body, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DraftOutcome(false, default, await ReadMessageAsync(response, cancellationToken));
            }
            var draft = await response.Content.ReadFromJsonAsync<BankReconciliationDraftDto>(cancellationToken);
            return new DraftOutcome(true, draft, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {Verb}.", verb);
            return new DraftOutcome(false, default, "Unable to reach the server. Please try again.");
        }
    }

    private async Task<DraftOutcome> SendJsonAsync<T>(
        HttpMethod method,
        string url,
        object body,
        CancellationToken cancellationToken,
        string verb)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = JsonContent.Create(body)
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DraftOutcome(false, default, await ReadMessageAsync(response, cancellationToken));
            }
            var draft = await response.Content.ReadFromJsonAsync<BankReconciliationDraftDto>(cancellationToken);
            return new DraftOutcome(true, draft, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {Verb}.", verb);
            return new DraftOutcome(false, default, "Unable to reach the server. Please try again.");
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

// R-2: Bank Register DTOs. Wire-level shape of the
// /accounting/bank-register/{accountId} response.

public sealed record BankRegisterResponse(
    Guid AccountId,
    IReadOnlyList<BankRegisterEntryDto> Entries);

public sealed record BankRegisterEntryDto(
    BankReconciliationLedgerEntrySummary Entry,
    bool IsCleared,
    bool IsInDraft,
    Guid? ReconciliationId,
    DateOnly? ClearedOnStatementDate);

public sealed record BankRegisterOutcome(
    bool Succeeded,
    IReadOnlyList<BankRegisterEntryDto> Entries,
    string? ErrorMessage);

// R-3 draft lifecycle DTOs (wire mirror of Application.Reconciliation types).

public sealed record BankReconciliationDraftDto(
    Guid Id,
    Guid BankAccountId,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal EndingBalance,
    decimal ClearedIncrease,
    decimal ClearedDecrease,
    decimal CalculatedEndingBalance,
    decimal Difference,
    int ClearedLineCount,
    string? Notes,
    UserId CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt);

public sealed record BankReconciliationDraftCandidateDto(
    BankReconciliationLedgerEntrySummary Entry,
    bool ClearedInThisDraft);

public sealed record BankReconciliationDraftOpenPayload(
    Guid BankAccountId,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal EndingBalance,
    string? Notes);

public sealed record BankReconciliationDraftPatchPayload(
    decimal? OpeningBalance,
    decimal? EndingBalance,
    DateOnly? StatementDate,
    string? Notes);

public sealed record BankReconciliationDraftToggleDto(
    Guid LedgerEntryId,
    bool Cleared);

public sealed record DraftCandidatesResponseDto(
    Guid DraftId,
    IReadOnlyList<BankReconciliationDraftCandidateDto> Candidates);

public sealed record DraftOutcome(
    bool Succeeded,
    BankReconciliationDraftDto? Draft,
    string? ErrorMessage);

public sealed record DraftCandidatesOutcome(
    bool Succeeded,
    IReadOnlyList<BankReconciliationDraftCandidateDto> Candidates,
    string? ErrorMessage);

public sealed record BankReconciliationActionOutcome(
    bool Succeeded,
    string? ErrorMessage);

// R-4 carry-forward + report DTOs.

public sealed record BankReconciliationLastCompletedDto(
    Guid ReconciliationId,
    DateOnly StatementDate,
    decimal EndingBalance,
    DateTimeOffset CompletedAt);

public sealed record BankReconciliationReportDto(
    Guid ReconciliationId,
    Guid BankAccountId,
    string BankAccountCode,
    string BankAccountName,
    DateOnly StatementDate,
    string Status,
    decimal OpeningBalance,
    decimal EndingBalance,
    decimal ClearedIncrease,
    decimal ClearedDecrease,
    decimal CalculatedEndingBalance,
    decimal Difference,
    int LineCount,
    string? Notes,
    UserId? CreatedByUserId,
    DateTimeOffset CreatedAt,
    UserId? CompletedByUserId,
    DateTimeOffset? CompletedAt,
    UserId? AbandonedByUserId,
    DateTimeOffset? AbandonedAt,
    IReadOnlyList<BankReconciliationReportLineDto> Lines);

public sealed record BankReconciliationReportLineDto(
    Guid LedgerEntryId,
    Guid JournalEntryId,
    Guid JournalEntryLineId,
    DateOnly PostingDate,
    string DisplayNumber,
    string AccountCode,
    string AccountName,
    string Description,
    string TransactionCurrencyCode,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    decimal SignedAmountBase,
    decimal SignedAmountTransaction);
