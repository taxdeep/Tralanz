using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Journal;
using SharedKernel.Identity;

namespace Tests.GL;

/// <summary>
/// M6: defensive double-entry balance enforcement on the
/// <see cref="JournalEntry"/> domain ctor. The authoritative balance
/// check still lives in <c>DefaultPostingSupport.EnsureJournalInvariants</c>
/// (full posting-engine invariants) and the writer's
/// <c>EnsureDraftIsBalanced</c>; these tests pin down the early-detection
/// behavior so any future regression of the ctor check fails loudly.
/// </summary>
public sealed class JournalEntryDomainBalanceTests
{
    [Fact]
    public void Balanced_journal_entry_constructs_without_throwing()
    {
        var lines = new[]
        {
            Line(1, debit: 100m, credit: 0m, txDebit: 100m, txCredit: 0m),
            Line(2, debit: 0m, credit: 100m, txDebit: 0m, txCredit: 100m),
        };

        var je = NewJournalEntry(lines);

        Assert.Equal(2, je.Lines.Count);
    }

    [Fact]
    public void Unbalanced_base_currency_throws_with_delta_in_message()
    {
        var lines = new[]
        {
            Line(1, debit: 100m, credit: 0m, txDebit: 100m, txCredit: 0m),
            // Credit short by 5.00 on the base axis. TX axis still balanced.
            Line(2, debit: 0m, credit: 95m, txDebit: 0m, txCredit: 100m),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => NewJournalEntry(lines));
        Assert.Contains("base currency", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public void Unbalanced_transaction_currency_throws_with_delta_in_message()
    {
        var lines = new[]
        {
            // TX-axis short by 7.00; base axis still balanced.
            Line(1, debit: 100m, credit: 0m, txDebit: 100m, txCredit: 0m),
            Line(2, debit: 0m, credit: 100m, txDebit: 0m, txCredit: 93m),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => NewJournalEntry(lines));
        Assert.Contains("transaction currency", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7", ex.Message);
    }

    [Fact]
    public void Realized_fx_leg_with_tx_zero_passes_when_base_still_balances()
    {
        // Three-leg shape: a regular pair (Dr cash / Cr revenue) plus
        // a realized-FX adjustment that only moves the base side.
        // The FX leg has TxDebit = TxCredit = 0 by design — TX axis
        // is 100 vs 100 (still balanced); base axis is 137 vs (130+7).
        var lines = new[]
        {
            Line(1, debit: 137m, credit: 0m, txDebit: 100m, txCredit: 0m),
            Line(2, debit: 0m, credit: 130m, txDebit: 0m, txCredit: 100m),
            Line(3, debit: 0m, credit: 7m, txDebit: 0m, txCredit: 0m),
        };

        var je = NewJournalEntry(lines);

        Assert.Equal(3, je.Lines.Count);
    }

    [Fact]
    public void Six_decimal_rounding_tolerance_treats_sub_ulp_drift_as_balanced()
    {
        // 1e-7 difference on each axis — rounds to zero at 6 decimals.
        var lines = new[]
        {
            Line(1, debit: 100.0000001m, credit: 0m, txDebit: 100.0000001m, txCredit: 0m),
            Line(2, debit: 0m, credit: 100m,        txDebit: 0m,            txCredit: 100m),
        };

        var je = NewJournalEntry(lines);
        Assert.Equal(2, je.Lines.Count);
    }

    private static JournalEntryLine Line(
        int lineNumber, decimal debit, decimal credit, decimal txDebit, decimal txCredit) =>
        new(LineNumber: lineNumber,
            AccountId: Guid.NewGuid(),
            Description: $"line {lineNumber}",
            TxDebit: txDebit,
            TxCredit: txCredit,
            Debit: debit,
            Credit: credit);

    private static JournalEntry NewJournalEntry(IEnumerable<JournalEntryLine> lines)
    {
        var usd = new CurrencyCode("USD");
        var cad = new CurrencyCode("CAD");
        var snapshot = new FxSnapshotRef(
            SnapshotId: Guid.NewGuid(),
            BaseCurrencyCode: cad,
            QuoteCurrencyCode: usd,
            Rate: 1.37m,
            RequestedDate: new DateOnly(2026, 5, 22),
            EffectiveDate: new DateOnly(2026, 5, 22),
            SourceSemantics: "manual");

        return new JournalEntry(
            id: Guid.NewGuid(),
            companyId: CompanyId.FromOrdinal(1),
            entityNumber: EntityNumber.Create(2026, 1),
            displayNumber: new DocumentNumber("JE-2026-0001"),
            status: "draft",
            sourceType: "test",
            sourceId: Guid.NewGuid(),
            transactionCurrencyCode: usd,
            baseCurrencyCode: cad,
            fxSnapshot: snapshot,
            lines: lines,
            postingRunId: PostingRunId.New(),
            idempotencyKey: "test:" + Guid.NewGuid());
    }
}
