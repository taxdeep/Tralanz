namespace Citus.Ui.Shared.Reports;

public sealed record class IncomeStatementReportSummary
{
    public CompanyId CompanyId { get; init; }

    public DateOnly DateFrom { get; init; }

    public DateOnly DateTo { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool IncludeZeroBalanceAccounts { get; init; }

    public int AccountCount { get; init; }

    public decimal TotalRevenue { get; init; }

    public decimal TotalCostOfSales { get; init; }

    public decimal GrossProfit { get; init; }

    public decimal TotalExpenses { get; init; }

    public decimal NetIncome { get; init; }

    public IReadOnlyList<IncomeStatementAccountSummary> RevenueRows { get; init; } = Array.Empty<IncomeStatementAccountSummary>();

    public IReadOnlyList<IncomeStatementAccountSummary> CostOfSalesRows { get; init; } = Array.Empty<IncomeStatementAccountSummary>();

    public IReadOnlyList<IncomeStatementAccountSummary> ExpenseRows { get; init; } = Array.Empty<IncomeStatementAccountSummary>();
}
