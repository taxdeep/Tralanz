namespace Citus.Accounting.Application.Queries;

/// <summary>
/// One raw contribution to a cash-basis P&amp;L account, before aggregation.
/// For "direct" P&amp;L activity (not originated by an invoice/bill) the fraction
/// is 1 (Numerator = Denominator). For invoice/bill payments the fraction is
/// <c>applied ÷ original</c> (in document currency) of that document's posted
/// P&amp;L journal-entry line. Debit/Credit are the document's GL line amounts in
/// BASE currency, so the cash-basis amount inherits the accrual account split
/// (tax already folded correctly) at the original FX rate.
/// </summary>
public sealed record CashBasisContributionRow(
    Guid AccountId,
    decimal FractionNumerator,
    decimal FractionDenominator,
    decimal Debit,
    decimal Credit);

public sealed record CashBasisAccountTotal(decimal Debit, decimal Credit);

/// <summary>
/// Pure aggregation of cash-basis contributions into per-account debit/credit
/// totals. This is the correctness-critical proportional math (partial
/// payments, multi-account invoices, repeated applications) — kept pure so it
/// is unit-tested without a database. The SQL only fetches the raw rows.
/// </summary>
public static class CashBasisIncomeMath
{
    public static IReadOnlyDictionary<Guid, CashBasisAccountTotal> Aggregate(
        IEnumerable<CashBasisContributionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var totals = new Dictionary<Guid, (decimal Debit, decimal Credit)>();

        foreach (var row in rows)
        {
            // A zero/negative denominator can't yield a meaningful fraction —
            // treat as no recognition rather than dividing by zero.
            var fraction = row.FractionDenominator == 0m
                ? 0m
                : row.FractionNumerator / row.FractionDenominator;

            totals.TryGetValue(row.AccountId, out var current);

            totals[row.AccountId] = (
                current.Debit + (fraction * row.Debit),
                current.Credit + (fraction * row.Credit));
        }

        // Round once per account at the end so per-contribution rounding does
        // not accumulate.
        return totals.ToDictionary(
            kvp => kvp.Key,
            kvp => new CashBasisAccountTotal(Round6(kvp.Value.Debit), Round6(kvp.Value.Credit)));
    }

    private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.ToEven);
}
