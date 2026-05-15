using Citus.Accounting.Domain.Common;
using SharedKernel.Identity;

namespace Citus.Accounting.Application.Reconciliation;

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
