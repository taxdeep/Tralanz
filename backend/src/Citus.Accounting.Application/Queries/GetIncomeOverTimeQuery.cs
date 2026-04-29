using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Queries;

/// <summary>
/// Sales Overview income-over-time chart query. Aggregates posted-invoice
/// total amount (base currency, accrual basis) by month for a window the
/// caller chooses. The <c>compareToPrevYear</c> flag stacks an extra
/// year's worth of data lined up to the same month-of-year for an easy
/// year-over-year comparison.
/// </summary>
public sealed record class GetIncomeOverTimeQuery(
    CompanyId CompanyId,
    DateOnly FromDate,
    DateOnly ToDate,
    bool CompareToPreviousYear);

public sealed record class IncomeOverTimeMonthBucket
{
    public int Year { get; init; }

    public int Month { get; init; }

    public DateOnly MonthStart { get; init; }

    /// <summary>
    /// Posted-invoice amount (base currency) issued in this month — i.e.
    /// accrual-basis revenue, not cash collected.
    /// </summary>
    public decimal AmountBase { get; init; }
}

public sealed record class IncomeOverTimeReport
{
    public Guid CompanyId { get; init; }

    public DateOnly FromDate { get; init; }

    public DateOnly ToDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool CompareToPreviousYear { get; init; }

    /// <summary>
    /// One bucket per month in the [FromDate, ToDate] window, including
    /// zero-amount months so the chart line draws continuously.
    /// </summary>
    public IReadOnlyList<IncomeOverTimeMonthBucket> Months { get; init; } =
        Array.Empty<IncomeOverTimeMonthBucket>();

    /// <summary>
    /// Same window shifted one year earlier — populated only when
    /// <see cref="CompareToPreviousYear"/> is true.
    /// </summary>
    public IReadOnlyList<IncomeOverTimeMonthBucket> PreviousYearMonths { get; init; } =
        Array.Empty<IncomeOverTimeMonthBucket>();
}
