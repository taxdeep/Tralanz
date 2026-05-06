namespace Citus.Ui.Shared.Reports;

public sealed record class TrialBalanceReportSummary
{
    public CompanyId CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool IncludeZeroBalanceAccounts { get; init; }

    public int AccountCount { get; init; }

    public decimal TotalBalanceDebit { get; init; }

    public decimal TotalBalanceCredit { get; init; }

    public bool IsBalanced { get; init; }

    public IReadOnlyList<TrialBalanceAccountSummary> Rows { get; init; } = Array.Empty<TrialBalanceAccountSummary>();
}
