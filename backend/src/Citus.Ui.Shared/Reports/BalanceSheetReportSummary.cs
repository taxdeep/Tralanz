namespace Citus.Ui.Shared.Reports;

public sealed record class BalanceSheetReportSummary
{
    public CompanyId CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool IncludeZeroBalanceAccounts { get; init; }

    public int AccountCount { get; init; }

    public decimal TotalAssets { get; init; }

    public decimal TotalLiabilities { get; init; }

    public decimal CurrentEarnings { get; init; }

    public decimal TotalEquity { get; init; }

    public decimal TotalLiabilitiesAndEquity { get; init; }

    public bool IsBalanced { get; init; }

    public IReadOnlyList<BalanceSheetAccountSummary> AssetRows { get; init; } = Array.Empty<BalanceSheetAccountSummary>();

    public IReadOnlyList<BalanceSheetAccountSummary> LiabilityRows { get; init; } = Array.Empty<BalanceSheetAccountSummary>();

    public IReadOnlyList<BalanceSheetAccountSummary> EquityRows { get; init; } = Array.Empty<BalanceSheetAccountSummary>();
}
