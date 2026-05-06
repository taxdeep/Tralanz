using Citus.Accounting.Application.Queries;

namespace Citus.Accounting.Api.Tests;

public sealed class ArAgingReportProjectionTests
{
    [Fact]
    public void OpenItemCreate_AssignsPastDueBucketFromDueDate()
    {
        var row = ArAgingOpenItemAmount.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "EN202600000401",
            "Acme Retail",
            customerIsActive: true,
            "invoice",
            Guid.NewGuid(),
            "INV-1001",
            new DateOnly(2026, 3, 15),
            new DateOnly(2026, 3, 31),
            "usd",
            "usd",
            "debit",
            "open",
            originalAmountTx: 500m,
            originalAmountBase: 500m,
            openAmountTx: 500m,
            openAmountBase: 500m,
            new DateOnly(2026, 4, 30));

        Assert.Equal(30, row.DaysPastDue);
        Assert.Equal("1_30", row.AgingBucket);
        Assert.Equal(500m, row.SignedOpenAmountBase);
    }

    [Fact]
    public void OpenItemCreate_MapsCreditBalanceAsNegativeOutstanding()
    {
        var row = ArAgingOpenItemAmount.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "EN202600000402",
            "Acme Retail",
            customerIsActive: true,
            "credit_note",
            Guid.NewGuid(),
            "CN-1001",
            new DateOnly(2026, 4, 5),
            new DateOnly(2026, 4, 20),
            "usd",
            "usd",
            "credit",
            "open",
            originalAmountTx: 120m,
            originalAmountBase: 120m,
            openAmountTx: 120m,
            openAmountBase: 120m,
            new DateOnly(2026, 4, 30));

        Assert.Equal(-120m, row.SignedOpenAmountBase);
        Assert.Equal(-120m, row.SignedOpenAmountTx);
        Assert.Equal("1_30", row.AgingBucket);
    }

    [Fact]
    public void ReportCreate_GroupsCustomersAndTotalsBuckets()
    {
        var customerId = Guid.NewGuid();

        var report = ArAgingReport.Create(
            CompanyId.FromOrdinal(1),
            new DateOnly(2026, 4, 30),
            "usd",
            [
                ArAgingOpenItemAmount.Create(
                    Guid.NewGuid(),
                    customerId,
                    "EN202600000403",
                    "Acme Retail",
                    customerIsActive: true,
                    "invoice",
                    Guid.NewGuid(),
                    "INV-1002",
                    new DateOnly(2026, 4, 1),
                    new DateOnly(2026, 5, 15),
                    "usd",
                    "usd",
                    "debit",
                    "open",
                    originalAmountTx: 250m,
                    originalAmountBase: 250m,
                    openAmountTx: 250m,
                    openAmountBase: 250m,
                    new DateOnly(2026, 4, 30)),
                ArAgingOpenItemAmount.Create(
                    Guid.NewGuid(),
                    customerId,
                    "EN202600000403",
                    "Acme Retail",
                    customerIsActive: true,
                    "invoice",
                    Guid.NewGuid(),
                    "INV-1003",
                    new DateOnly(2026, 3, 1),
                    new DateOnly(2026, 3, 15),
                    "usd",
                    "usd",
                    "debit",
                    "partially_applied",
                    originalAmountTx: 400m,
                    originalAmountBase: 400m,
                    openAmountTx: 150m,
                    openAmountBase: 150m,
                    new DateOnly(2026, 4, 30)),
                ArAgingOpenItemAmount.Create(
                    Guid.NewGuid(),
                    customerId,
                    "EN202600000403",
                    "Acme Retail",
                    customerIsActive: true,
                    "credit_note",
                    Guid.NewGuid(),
                    "CN-1002",
                    new DateOnly(2026, 4, 10),
                    new DateOnly(2026, 4, 25),
                    "usd",
                    "usd",
                    "credit",
                    "open",
                    originalAmountTx: 50m,
                    originalAmountBase: 50m,
                    openAmountTx: 50m,
                    openAmountBase: 50m,
                    new DateOnly(2026, 4, 30))
            ]);

        Assert.Equal("USD", report.BaseCurrencyCode);
        Assert.Equal(1, report.CustomerCount);
        Assert.Equal(3, report.OpenItemCount);
        Assert.Equal(250m, report.CurrentAmountBase);
        Assert.Equal(150m, report.Days31To60AmountBase);
        Assert.Equal(350m, report.TotalOutstandingAmountBase);
        Assert.Equal(100m, report.TotalOverdueAmountBase);
        Assert.Single(report.CustomerRows);
        Assert.Equal(350m, report.CustomerRows[0].TotalOutstandingAmountBase);
    }
}
