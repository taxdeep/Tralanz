using Citus.Accounting.Application.Queries;

namespace Citus.Accounting.Api.Tests;

public sealed class ApAgingReportProjectionTests
{
    [Fact]
    public void OpenItemCreate_AssignsCreditBillAsPositiveOutstanding()
    {
        var row = ApAgingOpenItemAmount.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "EN20260000U",
            "North Harbor Supply",
            vendorIsActive: true,
            "bill",
            Guid.NewGuid(),
            "BILL-1001",
            new DateOnly(2026, 3, 1),
            new DateOnly(2026, 3, 31),
            "usd",
            "usd",
            "credit",
            "open",
            originalAmountTx: 400m,
            originalAmountBase: 400m,
            openAmountTx: 400m,
            openAmountBase: 400m,
            new DateOnly(2026, 4, 30));

        Assert.Equal(30, row.DaysPastDue);
        Assert.Equal("1_30", row.AgingBucket);
        Assert.Equal(400m, row.SignedOpenAmountBase);
    }

    [Fact]
    public void OpenItemCreate_MapsVendorCreditAsNegativeOutstanding()
    {
        var row = ApAgingOpenItemAmount.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "EN20260000U",
            "North Harbor Supply",
            vendorIsActive: true,
            "vendor_credit",
            Guid.NewGuid(),
            "VC-1001",
            new DateOnly(2026, 4, 5),
            new DateOnly(2026, 4, 25),
            "usd",
            "usd",
            "debit",
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
    public void ReportCreate_GroupsVendorsAndTotalsBuckets()
    {
        var vendorId = Guid.NewGuid();

        var report = ApAgingReport.Create(
            CompanyId.FromOrdinal(1),
            new DateOnly(2026, 4, 30),
            "usd",
            [
                ApAgingOpenItemAmount.Create(
                    Guid.NewGuid(),
                    vendorId,
                    "EN20260000U",
                    "North Harbor Supply",
                    vendorIsActive: true,
                    "bill",
                    Guid.NewGuid(),
                    "BILL-1002",
                    new DateOnly(2026, 4, 1),
                    new DateOnly(2026, 5, 15),
                    "usd",
                    "usd",
                    "credit",
                    "open",
                    originalAmountTx: 250m,
                    originalAmountBase: 250m,
                    openAmountTx: 250m,
                    openAmountBase: 250m,
                    new DateOnly(2026, 4, 30)),
                ApAgingOpenItemAmount.Create(
                    Guid.NewGuid(),
                    vendorId,
                    "EN20260000U",
                    "North Harbor Supply",
                    vendorIsActive: true,
                    "bill",
                    Guid.NewGuid(),
                    "BILL-1003",
                    new DateOnly(2026, 3, 1),
                    new DateOnly(2026, 3, 15),
                    "usd",
                    "usd",
                    "credit",
                    "partially_applied",
                    originalAmountTx: 400m,
                    originalAmountBase: 400m,
                    openAmountTx: 150m,
                    openAmountBase: 150m,
                    new DateOnly(2026, 4, 30)),
                ApAgingOpenItemAmount.Create(
                    Guid.NewGuid(),
                    vendorId,
                    "EN20260000U",
                    "North Harbor Supply",
                    vendorIsActive: true,
                    "vendor_credit",
                    Guid.NewGuid(),
                    "VC-1002",
                    new DateOnly(2026, 4, 10),
                    new DateOnly(2026, 4, 25),
                    "usd",
                    "usd",
                    "debit",
                    "open",
                    originalAmountTx: 50m,
                    originalAmountBase: 50m,
                    openAmountTx: 50m,
                    openAmountBase: 50m,
                    new DateOnly(2026, 4, 30))
            ]);

        Assert.Equal("USD", report.BaseCurrencyCode);
        Assert.Equal(1, report.VendorCount);
        Assert.Equal(3, report.OpenItemCount);
        Assert.Equal(250m, report.CurrentAmountBase);
        Assert.Equal(150m, report.Days31To60AmountBase);
        Assert.Equal(350m, report.TotalOutstandingAmountBase);
        Assert.Equal(100m, report.TotalOverdueAmountBase);
        Assert.Single(report.VendorRows);
        Assert.Equal(350m, report.VendorRows[0].TotalOutstandingAmountBase);
    }
}
