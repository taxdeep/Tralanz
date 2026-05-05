namespace Citus.Ui.Shared.Reports;

public sealed record class IncomeOverTimeSummary
{
    public CompanyId CompanyId { get; init; }

    public DateOnly FromDate { get; init; }

    public DateOnly ToDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool CompareToPreviousYear { get; init; }

    public IReadOnlyList<IncomeOverTimeMonthSummary> Months { get; init; } =
        Array.Empty<IncomeOverTimeMonthSummary>();

    public IReadOnlyList<IncomeOverTimeMonthSummary> PreviousYearMonths { get; init; } =
        Array.Empty<IncomeOverTimeMonthSummary>();
}

public sealed record class IncomeOverTimeMonthSummary
{
    public int Year { get; init; }

    public int Month { get; init; }

    public DateOnly MonthStart { get; init; }

    public decimal AmountBase { get; init; }
}
