using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Queries;

/// <summary>
/// Sales Overview cash-flow band query: 10 historical months + the
/// current month + 3 forecast months, anchored on <see cref="AsOfDate"/>.
/// Past + current months pull received cash from posted receive-payment
/// documents (cash basis); the 3 forecast months sum the open AR
/// balance whose due date lands in that month (invoice + payment term).
/// </summary>
public sealed record class GetSalesCashFlowQuery(
    CompanyId CompanyId,
    DateOnly AsOfDate);

public sealed record class SalesCashFlowMonthBucket
{
    public int Year { get; init; }

    public int Month { get; init; }

    /// <summary>First-of-month date for this bucket (always day 1).</summary>
    public DateOnly MonthStart { get; init; }

    /// <summary>true for buckets whose month is strictly after the as-of month.</summary>
    public bool IsForecast { get; init; }

    /// <summary>true for the bucket whose month equals the as-of month.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>
    /// Cash received in this month from posted receive-payment documents,
    /// converted to base currency at the payment's stored fx rate.
    /// Always 0 for forecast months.
    /// </summary>
    public decimal ReceivedAmountBase { get; init; }

    /// <summary>
    /// For forecast months: sum of open AR balances (signed, base) whose
    /// due_date falls in this month. 0 for past + current months.
    /// </summary>
    public decimal ForecastAmountBase { get; init; }
}

public sealed record class SalesCashFlowReport
{
    public CompanyId CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public IReadOnlyList<SalesCashFlowMonthBucket> Months { get; init; } =
        Array.Empty<SalesCashFlowMonthBucket>();
}
