using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Queries;

/// <summary>
/// Expense Overview "expense over time" chart query — accrual-basis
/// cost per month, sourced from posted bills (bill_date) plus posted
/// expenses (payment_date). Mirror of
/// <see cref="GetIncomeOverTimeQuery"/> on the AP side.
/// </summary>
public sealed record class GetExpenseOverTimeQuery(
    CompanyId CompanyId,
    DateOnly FromDate,
    DateOnly ToDate,
    bool CompareToPreviousYear);

public sealed record class ExpenseOverTimeMonthBucket
{
    public int Year { get; init; }

    public int Month { get; init; }

    public DateOnly MonthStart { get; init; }

    /// <summary>
    /// Posted-bill amount + posted-expense amount issued in this
    /// month (base currency). Bills follow accrual-basis (bill_date);
    /// expenses use payment_date because they are cash-only documents
    /// without a separate accrual milestone.
    /// </summary>
    public decimal AmountBase { get; init; }
}

public sealed record class ExpenseOverTimeReport
{
    public Guid CompanyId { get; init; }

    public DateOnly FromDate { get; init; }

    public DateOnly ToDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool CompareToPreviousYear { get; init; }

    public IReadOnlyList<ExpenseOverTimeMonthBucket> Months { get; init; } =
        Array.Empty<ExpenseOverTimeMonthBucket>();

    public IReadOnlyList<ExpenseOverTimeMonthBucket> PreviousYearMonths { get; init; } =
        Array.Empty<ExpenseOverTimeMonthBucket>();
}
