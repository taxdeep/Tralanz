using Citus.Accounting.Application.Queries;

namespace Citus.Accounting.Api.Tests;

public sealed class IncomeStatementReportProjectionTests
{
    [Fact]
    public void AccountAmountCreate_MapsRevenueAsCreditLessDebit()
    {
        var row = IncomeStatementAccountAmount.Create(
            Guid.NewGuid(),
            "EN202600000201",
            "4100",
            "Service Revenue",
            "revenue",
            "service_revenue",
            isActive: true,
            isSystem: false,
            postedDebitTotal: 25m,
            postedCreditTotal: 250m);

        Assert.Equal(225m, row.DisplayAmount);
        Assert.True(row.HasActivity);
    }

    [Fact]
    public void AccountAmountCreate_MapsExpenseAsDebitLessCredit()
    {
        var row = IncomeStatementAccountAmount.Create(
            Guid.NewGuid(),
            "EN202600000202",
            "6100",
            "Office Expense",
            "expense",
            "operating_expense",
            isActive: true,
            isSystem: false,
            postedDebitTotal: 180m,
            postedCreditTotal: 15m);

        Assert.Equal(165m, row.DisplayAmount);
    }

    [Fact]
    public void ReportCreate_ComputesGrossProfitAndNetIncome()
    {
        var report = IncomeStatementReport.Create(
            Guid.NewGuid(),
            new DateOnly(2026, 4, 1),
            new DateOnly(2026, 4, 30),
            "usd",
            includeZeroBalanceAccounts: false,
            [
                IncomeStatementAccountAmount.Create(
                    Guid.NewGuid(),
                    "EN202600000203",
                    "4100",
                    "Service Revenue",
                    "revenue",
                    "service_revenue",
                    isActive: true,
                    isSystem: false,
                    postedDebitTotal: 0m,
                    postedCreditTotal: 900m),
                IncomeStatementAccountAmount.Create(
                    Guid.NewGuid(),
                    "EN202600000204",
                    "5100",
                    "Cost Of Sales",
                    "cost_of_sales",
                    "cost_of_sales",
                    isActive: true,
                    isSystem: false,
                    postedDebitTotal: 250m,
                    postedCreditTotal: 0m),
                IncomeStatementAccountAmount.Create(
                    Guid.NewGuid(),
                    "EN202600000205",
                    "6100",
                    "Office Expense",
                    "expense",
                    "operating_expense",
                    isActive: true,
                    isSystem: false,
                    postedDebitTotal: 150m,
                    postedCreditTotal: 0m),
                IncomeStatementAccountAmount.Create(
                    Guid.NewGuid(),
                    "EN202600000206",
                    "6999",
                    "Unused",
                    "expense",
                    "misc",
                    isActive: false,
                    isSystem: false,
                    postedDebitTotal: 0m,
                    postedCreditTotal: 0m)
            ]);

        Assert.Equal("USD", report.BaseCurrencyCode);
        Assert.Equal(3, report.AccountCount);
        Assert.Equal(900m, report.TotalRevenue);
        Assert.Equal(250m, report.TotalCostOfSales);
        Assert.Equal(650m, report.GrossProfit);
        Assert.Equal(150m, report.TotalExpenses);
        Assert.Equal(500m, report.NetIncome);
        Assert.Single(report.RevenueRows);
        Assert.Single(report.CostOfSalesRows);
        Assert.Single(report.ExpenseRows);
    }
}
