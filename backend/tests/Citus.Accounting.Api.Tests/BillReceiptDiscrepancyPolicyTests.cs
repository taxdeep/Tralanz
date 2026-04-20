using Citus.Accounting.Application;

namespace Citus.Accounting.Api.Tests;

public sealed class BillReceiptDiscrepancyPolicyTests
{
    [Theory]
    [InlineData("no_receipt", "missing_receipt_coverage")]
    [InlineData("partially_covered", "partial_receipt_coverage")]
    [InlineData("fully_covered", null)]
    [InlineData("no_inventory_handoff", null)]
    public void ResolveDiscrepancyType_MapsOnlyOpenCoverageGaps(string matchStatus, string? expected)
    {
        Assert.Equal(expected, BillReceiptDiscrepancyPolicy.ResolveDiscrepancyType(matchStatus));
    }

    [Fact]
    public void BuildDiscrepancySummary_UsesRemainingQuantityAndAnchor()
    {
        var summary = BillReceiptDiscrepancyPolicy.BuildDiscrepancySummary(
            "partial_receipt_coverage",
            "RM-100",
            "MAIN",
            3.5m,
            "EA");

        Assert.Contains("RM-100", summary);
        Assert.Contains("MAIN", summary);
        Assert.Contains("3.50", summary);
        Assert.Contains("EA", summary);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1 inbound discrepancy lane is still open.")]
    [InlineData(3, "3 inbound discrepancy lanes are still open.")]
    public void BuildBrowserSummary_UsesOpenDiscrepancyCount(int count, string? expected)
    {
        Assert.Equal(expected, BillReceiptDiscrepancyPolicy.BuildBrowserSummary(count));
    }
}
