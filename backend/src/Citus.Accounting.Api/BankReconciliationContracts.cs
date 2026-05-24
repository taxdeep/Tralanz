using Citus.Accounting.Application.Reconciliation;

namespace Citus.Accounting.Api;

public sealed record BankReconciliationLedgerResponse(
    IReadOnlyList<BankReconciliationLedgerEntry> Entries);

public sealed record BankReconciliationCompleteHttpRequest(
    Guid BankAccountId,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal EndingBalance,
    IReadOnlyList<Guid> LedgerEntryIds,
    string? Notes);

// R-1: draft lifecycle DTOs. See BANKING_RECONCILE_PLAN.md Section 10.

public sealed record BankReconciliationDraftOpenHttpRequest(
    Guid BankAccountId,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal EndingBalance,
    string? Notes);

public sealed record BankReconciliationDraftPatchHttpRequest(
    decimal? OpeningBalance,
    decimal? EndingBalance,
    DateOnly? StatementDate,
    string? Notes);

public sealed record BankReconciliationDraftToggleHttpRequest(
    Guid LedgerEntryId,
    bool Cleared);

public sealed record BankReconciliationDraftCandidatesResponse(
    Guid DraftId,
    IReadOnlyList<BankReconciliationDraftCandidate> Candidates);
