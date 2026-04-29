namespace Citus.Ui.Shared.Reports;

public sealed record class ExpenseCashOutflowSummary
{
    public Guid CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public IReadOnlyList<ExpenseCashOutflowMonthSummary> Months { get; init; } =
        Array.Empty<ExpenseCashOutflowMonthSummary>();
}

public sealed record class ExpenseCashOutflowMonthSummary
{
    public int Year { get; init; }

    public int Month { get; init; }

    public DateOnly MonthStart { get; init; }

    public bool IsForecast { get; init; }

    public bool IsCurrent { get; init; }

    public decimal PaidAmountBase { get; init; }

    public decimal ForecastAmountBase { get; init; }
}
