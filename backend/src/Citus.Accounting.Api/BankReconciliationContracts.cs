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
