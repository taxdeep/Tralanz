namespace Citus.Ui.Shared.Reports;

public sealed record class SalesCashFlowSummary
{
    public Guid CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public IReadOnlyList<SalesCashFlowMonthSummary> Months { get; init; } =
        Array.Empty<SalesCashFlowMonthSummary>();
}

public sealed record class SalesCashFlowMonthSummary
{
    public int Year { get; init; }

    public int Month { get; init; }

    public DateOnly MonthStart { get; init; }

    public bool IsForecast { get; init; }

    public bool IsCurrent { get; init; }

    public decimal ReceivedAmountBase { get; init; }

    public decimal ForecastAmountBase { get; init; }
}
