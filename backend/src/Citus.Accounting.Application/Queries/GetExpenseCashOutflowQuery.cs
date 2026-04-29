using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Queries;

/// <summary>
/// Expense Overview cash-outflow band query — mirror of
/// <see cref="GetSalesCashFlowQuery"/> on the AP side. Past + current
/// months pull cash that left the company (posted pay_bills +
/// posted expenses, both grouped by payment_date); the 3 forecast
/// months sum the open AP balance whose due date lands in that month
/// (bill + payment term).
/// </summary>
public sealed record class GetExpenseCashOutflowQuery(
    CompanyId CompanyId,
    DateOnly AsOfDate);

public sealed record class ExpenseCashOutflowMonthBucket
{
    public int Year { get; init; }

    public int Month { get; init; }

    public DateOnly MonthStart { get; init; }

    public bool IsForecast { get; init; }

    public bool IsCurrent { get; init; }

    /// <summary>
    /// Cash paid in this month — pay_bills.total_amount * fx_rate
    /// plus expenses.total_amount * fx_rate, both filtered to status
    /// 'posted'. 0 for forecast months.
    /// </summary>
    public decimal PaidAmountBase { get; init; }

    /// <summary>
    /// For forecast months: open AP balance (signed, base) whose
    /// due_date falls in this month. 0 for past + current months.
    /// </summary>
    public decimal ForecastAmountBase { get; init; }
}

public sealed record class ExpenseCashOutflowReport
{
    public Guid CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public IReadOnlyList<ExpenseCashOutflowMonthBucket> Months { get; init; } =
        Array.Empty<ExpenseCashOutflowMonthBucket>();
}
