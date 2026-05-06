namespace Citus.Ui.Shared.Reports;

public sealed record class ExpenseOverTimeSummary
{
    public CompanyId CompanyId { get; init; }

    public DateOnly FromDate { get; init; }

    public DateOnly ToDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool CompareToPreviousYear { get; init; }

    public IReadOnlyList<ExpenseOverTimeMonthSummary> Months { get; init; } =
        Array.Empty<ExpenseOverTimeMonthSummary>();

    public IReadOnlyList<ExpenseOverTimeMonthSummary> PreviousYearMonths { get; init; } =
        Array.Empty<ExpenseOverTimeMonthSummary>();
}

public sealed record class ExpenseOverTimeMonthSummary
{
    public int Year { get; init; }

    public int Month { get; init; }

    public DateOnly MonthStart { get; init; }

    public decimal AmountBase { get; init; }
}
