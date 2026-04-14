using Citus.Accounting.Application.Queries;

namespace Citus.Accounting.Api.Tests;

public sealed class TrialBalanceReportProjectionTests
{
    [Fact]
    public void AccountBalanceCreate_MapsDebitSide_WhenDebitsExceedCredits()
    {
        var row = TrialBalanceAccountBalance.Create(
            Guid.NewGuid(),
            "EN202600000001",
            "1000",
            "Cash",
            "asset",
            "cash",
            isActive: true,
            isSystem: false,
            postedDebitTotal: 125.50m,
            postedCreditTotal: 25.25m);

        Assert.Equal(100.25m, row.BalanceDebit);
        Assert.Equal(0m, row.BalanceCredit);
        Assert.Equal(100.25m, row.NetBalance);
        Assert.Equal("debit", row.BalanceSide);
        Assert.True(row.HasBalance);
    }

    [Fact]
    public void AccountBalanceCreate_MapsCreditSide_WhenCreditsExceedDebits()
    {
        var row = TrialBalanceAccountBalance.Create(
            Guid.NewGuid(),
            "EN202600000002",
            "3000",
            "Capital",
            "equity",
            "capital",
            isActive: true,
            isSystem: true,
            postedDebitTotal: 10m,
            postedCreditTotal: 160m);

        Assert.Equal(0m, row.BalanceDebit);
        Assert.Equal(150m, row.BalanceCredit);
        Assert.Equal(-150m, row.NetBalance);
        Assert.Equal("credit", row.BalanceSide);
    }

    [Fact]
    public void ReportCreate_FiltersZeroBalanceAccounts_WhenRequested()
    {
        var report = TrialBalanceReport.Create(
            Guid.NewGuid(),
            new DateOnly(2026, 4, 13),
            "usd",
            includeZeroBalanceAccounts: false,
            [
                TrialBalanceAccountBalance.Create(
                    Guid.NewGuid(),
                    "EN202600000003",
                    "1000",
                    "Cash",
                    "asset",
                    "cash",
                    isActive: true,
                    isSystem: false,
                    postedDebitTotal: 50m,
                    postedCreditTotal: 0m),
                TrialBalanceAccountBalance.Create(
                    Guid.NewGuid(),
                    "EN202600000004",
                    "2000",
                    "Accounts Payable",
                    "liability",
                    "ap",
                    isActive: true,
                    isSystem: false,
                    postedDebitTotal: 0m,
                    postedCreditTotal: 50m),
                TrialBalanceAccountBalance.Create(
                    Guid.NewGuid(),
                    "EN202600000005",
                    "9999",
                    "Unused",
                    "expense",
                    "misc",
                    isActive: false,
                    isSystem: false,
                    postedDebitTotal: 0m,
                    postedCreditTotal: 0m)
            ]);

        Assert.Equal("USD", report.BaseCurrencyCode);
        Assert.Equal(2, report.AccountCount);
        Assert.Equal(50m, report.TotalBalanceDebit);
        Assert.Equal(50m, report.TotalBalanceCredit);
        Assert.True(report.IsBalanced);
        Assert.DoesNotContain(report.Rows, row => row.Code == "9999");
    }
}
