using Citus.Accounting.Domain.Common;
using SharedKernel.Identity;

namespace Citus.Accounting.Application.Reconciliation;

/// <summary>
/// R-1: lifecycle states for bank_reconciliations rows.
/// in_progress = draft (operator marking cleared, can leave / resume).
/// completed   = signed off, JE lines locked, snapshot stored in lines table.
/// abandoned   = "Close without saving" or undone after completion.
///               Row is retained for audit, not reused.
/// Wire tokens match the bank_reconciliations.status CHECK constraint.
/// </summary>
public enum BankReconciliationStatus
{
    InProgress,
    Completed,
    Abandoned
}

public static class BankReconciliationStatusTokens
{
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Abandoned = "abandoned";

    public static string ToToken(this BankReconciliationStatus status) => status switch
    {
        BankReconciliationStatus.InProgress => InProgress,
        BankReconciliationStatus.Completed => Completed,
        BankReconciliationStatus.Abandoned => Abandoned,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown bank reconciliation status.")
    };

    public static BankReconciliationStatus FromToken(string token) => token switch
    {
        InProgress => BankReconciliationStatus.InProgress,
        Completed => BankReconciliationStatus.Completed,
        Abandoned => BankReconciliationStatus.Abandoned,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown bank reconciliation status token.")
    };
}

public sealed record BankReconciliationLedgerEntry(
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

public sealed record BankReconciliationCalculation(
    decimal OpeningBalance,
    decimal ClearedIncrease,
    decimal ClearedDecrease,
    decimal CalculatedEndingBalance,
    decimal StatementEndingBalance,
    decimal Difference);

public sealed record BankReconciliationCompleteInput(
    Guid BankAccountId,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal EndingBalance,
    IReadOnlyList<Guid> LedgerEntryIds,
    string? Notes);

public sealed record BankReconciliationSummary(
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

/// <summary>
/// R-1: input to open a draft via POST /draft. The operator picks
/// the bank account, supplies the statement ending balance and date
/// (mandatory), and may override the auto-derived beginning balance
/// (defaults to the previous completed reconciliation's ending
/// balance). Notes are optional.
/// </summary>
public sealed record BankReconciliationDraftOpenInput(
    Guid BankAccountId,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal EndingBalance,
    string? Notes);

/// <summary>
/// R-1: input to PATCH /draft/{id}. All fields nullable — only the
/// provided ones change. Used by the "Edit info" side drawer.
/// </summary>
public sealed record BankReconciliationDraftPatchInput(
    decimal? OpeningBalance,
    decimal? EndingBalance,
    DateOnly? StatementDate,
    string? Notes);

/// <summary>
/// R-1: snapshot of an in-progress draft, with running totals
/// computed live from ledger_entries.reconciliation_draft_id = id.
/// Returned by every draft endpoint so the UI can re-render the
/// 4-card summary without a separate read.
/// </summary>
public sealed record BankReconciliationDraft(
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

/// <summary>
/// R-1: a candidate ledger entry shown on the reconcile workspace
/// row list. ClearedInThisDraft is the green-check state — true
/// when the entry's reconciliation_draft_id equals the current
/// draft's id.
/// </summary>
public sealed record BankReconciliationDraftCandidate(
    BankReconciliationLedgerEntry Entry,
    bool ClearedInThisDraft);

/// <summary>
/// R-2: a row in the Bank Register list. Same ledger entry payload
/// as the reconcile candidates, plus the row's current reconciliation
/// status (unreconciled / in someone's draft / cleared by a completed
/// reconciliation). The register shows EVERYTHING posted on a bank
/// account (including FX revaluation entries with tx_debit =
/// tx_credit = 0); the reconcile candidate list filters those out
/// per boundary B3, but the register surfaces them for transparency.
/// </summary>
public sealed record BankRegisterEntry(
    BankReconciliationLedgerEntry Entry,
    bool IsCleared,
    bool IsInDraft,
    Guid? ReconciliationId,
    DateOnly? ClearedOnStatementDate);

/// <summary>
/// R-4: lightweight summary of the previous reconciliation for an
/// account. Drives the carry-forward prefill on the Start
/// reconciling form. Returned by GetLastCompletedAsync; null when
/// the account has never been reconciled.
/// </summary>
public sealed record BankReconciliationLastCompleted(
    Guid ReconciliationId,
    DateOnly StatementDate,
    decimal EndingBalance,
    DateTimeOffset CompletedAt);

/// <summary>
/// R-4: full report payload for a completed or undone-and-now-
/// abandoned reconciliation. Header + frozen line snapshot from
/// bank_reconciliation_lines. Display columns (account name /
/// display number / description) come from CURRENT joined data —
/// the financial fields (signed amounts, debits, credits) are the
/// snapshot at completion time and are the audit anchor.
/// </summary>
public sealed record BankReconciliationReport(
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
    IReadOnlyList<BankReconciliationReportLine> Lines);

public sealed record BankReconciliationReportLine(
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

public interface IBankReconciliationStore
{
    Task<IReadOnlyList<BankReconciliationLedgerEntry>> ListUnreconciledLedgerEntriesAsync(
        CompanyId companyId,
        Guid bankAccountId,
        DateOnly statementDate,
        CancellationToken cancellationToken);

    Task<BankReconciliationSummary> CompleteAsync(
        CompanyId companyId,
        UserId completedByUserId,
        BankReconciliationCompleteInput input,
        CancellationToken cancellationToken);

    // ---------------------------------------------------------------
    // R-1 draft lifecycle methods. See BANKING_RECONCILE_PLAN.md
    // sections 7 (state machine), 9 (LIFO undo), 10 (API surface).
    // ---------------------------------------------------------------

    /// <summary>Open a new draft. Fails with InvalidOperationException
    /// ("draft_already_open_for_account") if one is already in flight
    /// for this (company, account), enforced by the partial unique
    /// index in the schema migration.</summary>
    Task<BankReconciliationDraft> OpenDraftAsync(
        CompanyId companyId,
        UserId createdByUserId,
        BankReconciliationDraftOpenInput input,
        CancellationToken cancellationToken);

    /// <summary>Load a draft by id. Returns null if the row doesn't
    /// exist OR is not in_progress. Used by Resume.</summary>
    Task<BankReconciliationDraft?> LoadDraftAsync(
        CompanyId companyId,
        Guid draftId,
        CancellationToken cancellationToken);

    /// <summary>Used by the Reconcile entry page to detect "is there
    /// a draft to resume for this account?". Returns null if none.</summary>
    Task<BankReconciliationDraft?> FindOpenDraftForAccountAsync(
        CompanyId companyId,
        Guid bankAccountId,
        CancellationToken cancellationToken);

    /// <summary>List candidates for this draft: every ledger entry on
    /// the bank account, posted, not in any completed reconciliation,
    /// FX-revaluation entries filtered out (tx_debit = tx_credit = 0,
    /// boundary B3). Each carries a flag indicating whether it is
    /// currently marked cleared in THIS draft.</summary>
    Task<IReadOnlyList<BankReconciliationDraftCandidate>> ListDraftCandidatesAsync(
        CompanyId companyId,
        Guid draftId,
        CancellationToken cancellationToken);

    /// <summary>Toggle a ledger entry's draft mark on or off. Returns
    /// the refreshed draft summary so the UI can redraw totals.</summary>
    Task<BankReconciliationDraft> ToggleLineAsync(
        CompanyId companyId,
        Guid draftId,
        Guid ledgerEntryId,
        bool cleared,
        CancellationToken cancellationToken);

    /// <summary>Update statement fields mid-session (Edit info drawer).
    /// Cleared selections are preserved.</summary>
    Task<BankReconciliationDraft> PatchStatementInfoAsync(
        CompanyId companyId,
        Guid draftId,
        BankReconciliationDraftPatchInput input,
        CancellationToken cancellationToken);

    /// <summary>"Close without saving". Clears every
    /// reconciliation_draft_id pointing at this draft, marks the
    /// header row status='abandoned'. The header is retained for
    /// audit (Section 7), not deleted.</summary>
    Task AbandonDraftAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid draftId,
        CancellationToken cancellationToken);

    /// <summary>Finalize a draft: difference must be < 0.005. Inside
    /// one SERIALIZABLE tx: snapshot the draft-marked entries into
    /// bank_reconciliation_lines, move ledger_entries.reconciliation_draft_id
    /// to reconciliation_id, status='completed'. After this call the
    /// referenced ledger entries are locked (Section 8).</summary>
    Task<BankReconciliationSummary> CompleteDraftAsync(
        CompanyId companyId,
        UserId completedByUserId,
        Guid draftId,
        CancellationToken cancellationToken);

    /// <summary>Undo a completed reconciliation. Strict LIFO: rejects
    /// with InvalidOperationException ("reconciliation_undo_not_latest")
    /// if any later completed reconciliation exists for the same
    /// account. On success the previously-cleared ledger entries
    /// become unreconciled again; the header row stays as
    /// status='abandoned' for audit.</summary>
    Task UndoCompletedAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid reconciliationId,
        CancellationToken cancellationToken);

    /// <summary>R-2: list ledger entries for a bank / cash / credit-card
    /// account, posting_date DESC, each row tagged with its
    /// reconciliation status. Includes FX revaluation entries (the
    /// reconcile candidate query filters them out, but the register
    /// surfaces them so operators can see why the base carrying value
    /// drifted from the transaction-currency cleared balance). Cursor
    /// pagination is R-5; V1 caps at 200 rows.</summary>
    Task<IReadOnlyList<BankRegisterEntry>> ListBankRegisterAsync(
        CompanyId companyId,
        Guid bankAccountId,
        int take,
        CancellationToken cancellationToken);

    /// <summary>R-4: most-recent completed reconciliation for the
    /// account. Drives carry-forward prefill of beginning balance
    /// on the next draft. Null when the account has no prior
    /// completed reconciliation (first-time setup).</summary>
    Task<BankReconciliationLastCompleted?> GetLastCompletedAsync(
        CompanyId companyId,
        Guid bankAccountId,
        CancellationToken cancellationToken);

    /// <summary>R-4: full report payload for a completed (or undone-
    /// and-abandoned) reconciliation. Returns null when the row
    /// doesn't exist or is still in_progress. For an abandoned row
    /// the snapshot lines may be empty (Undo deletes them); the
    /// header still includes who/when undid it.</summary>
    Task<BankReconciliationReport?> LoadReconciliationReportAsync(
        CompanyId companyId,
        Guid reconciliationId,
        CancellationToken cancellationToken);
}

public static class BankReconciliationPolicy
{
    public const decimal ZeroTolerance = 0.005m;

    public static BankReconciliationCalculation Calculate(
        decimal openingBalance,
        decimal statementEndingBalance,
        IEnumerable<BankReconciliationLedgerEntry> entries)
    {
        var increase = 0m;
        var decrease = 0m;

        foreach (var entry in entries)
        {
            if (entry.SignedAmountBase >= 0m)
            {
                increase += entry.SignedAmountBase;
            }
            else
            {
                decrease += Math.Abs(entry.SignedAmountBase);
            }
        }

        var calculatedEnding = openingBalance + increase - decrease;
        return new BankReconciliationCalculation(
            openingBalance,
            increase,
            decrease,
            calculatedEnding,
            statementEndingBalance,
            statementEndingBalance - calculatedEnding);
    }

    public static bool IsZeroDifference(decimal difference) =>
        Math.Abs(difference) < ZeroTolerance;
}
