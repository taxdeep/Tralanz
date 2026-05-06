using Citus.Accounting.Application.Queries;

namespace Citus.Accounting.Api.Tests;

public sealed class BalanceSheetReportProjectionTests
{
    [Fact]
    public void AccountAmountCreate_MapsAssetAsDebitLessCredit()
    {
        var row = BalanceSheetAccountAmount.Create(
            Guid.NewGuid(),
            "EN202600000301",
            "1010",
            "Cash",
            "asset",
            "cash",
            isActive: true,
            isSystem: false,
            postedDebitTotal: 320m,
            postedCreditTotal: 20m);

        Assert.Equal(300m, row.DisplayAmount);
        Assert.True(row.HasBalance);
    }

    [Fact]
    public void SyntheticCurrentEarnings_MapsPositiveNetIncomeIntoEquity()
    {
        var row = BalanceSheetAccountAmount.CreateSyntheticCurrentEarnings(450m);

        Assert.True(row.IsSynthetic);
        Assert.Equal("CURRENT-EARNINGS", row.Code);
        Assert.Equal("equity", row.RootType);
        Assert.Equal(450m, row.DisplayAmount);
        Assert.Equal(450m, row.PostedCreditTotal);
        Assert.Equal(0m, row.PostedDebitTotal);
    }

    [Fact]
    public void ReportCreate_IncludesCurrentEarningsAndBalances()
    {
        var report = BalanceSheetReport.Create(
            CompanyId.FromOrdinal(1),
            new DateOnly(2026, 4, 30),
            "usd",
            includeZeroBalanceAccounts: false,
            [
                BalanceSheetAccountAmount.Create(
                    Guid.NewGuid(),
                    "EN202600000302",
                    "1010",
                    "Cash",
                    "asset",
                    "cash",
                    isActive: true,
                    isSystem: false,
                    postedDebitTotal: 2200m,
                    postedCreditTotal: 0m),
                BalanceSheetAccountAmount.Create(
                    Guid.NewGuid(),
                    "EN202600000303",
                    "3100",
                    "Owner Capital",
                    "equity",
                    "capital",
                    isActive: true,
                    isSystem: false,
                    postedDebitTotal: 0m,
                    postedCreditTotal: 1750m),
                BalanceSheetAccountAmount.Create(
                    Guid.NewGuid(),
                    "EN202600000304",
                    "2999",
                    "Unused Liability",
                    "liability",
                    "misc",
                    isActive: true,
                    isSystem: false,
                    postedDebitTotal: 0m,
                    postedCreditTotal: 0m)
            ],
            currentEarnings: 450m);

        Assert.Equal("USD", report.BaseCurrencyCode);
        Assert.Equal(2_200m, report.TotalAssets);
        Assert.Equal(0m, report.TotalLiabilities);
        Assert.Equal(450m, report.CurrentEarnings);
        Assert.Equal(2_200m, report.TotalEquity);
        Assert.Equal(2_200m, report.TotalLiabilitiesAndEquity);
        Assert.True(report.IsBalanced);
        Assert.Single(report.AssetRows);
        Assert.Equal(2, report.EquityRows.Count);
    }
}
