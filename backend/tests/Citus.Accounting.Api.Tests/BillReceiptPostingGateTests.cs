using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Api.Tests;

public sealed class BillReceiptPostingGateTests
{
    [Theory]
    [InlineData("no_inventory_handoff", true)]
    [InlineData("fully_covered", true)]
    [InlineData("no_receipt", false)]
    [InlineData("partially_covered", false)]
    [InlineData("unexpected", false)]
    public void AllowsBillPost_MatchesReceiptFirstAuthority(string matchStatus, bool expected)
    {
        Assert.Equal(expected, BillReceiptPostingGate.AllowsBillPost(matchStatus));
    }

    [Fact]
    public void GetBlockedPostMessage_UsesRemainingQuantity_ForPartialReceipt()
    {
        var summary = new BillReceiptMatchingLaneSummary(
            Guid.NewGuid(),
            1,
            12m,
            1,
            4.5m,
            7.5m,
            "partially_covered",
            null,
            Array.Empty<BillReceiptMatchingReceiptSummary>(),
            Array.Empty<BillReceiptMatchingLineSummary>(),
            Array.Empty<BillReceiptMatchingDiscrepancySummary>());

        var message = BillReceiptPostingGate.GetBlockedPostMessage(summary);

        Assert.Contains("7.50", message);
        Assert.Contains("outstanding", message);
    }

    [Theory]
    [InlineData("no_receipt", "Post on hold: no receipt yet")]
    [InlineData("partially_covered", "Post on hold: receipt still partial")]
    [InlineData("fully_covered", "Post enabled")]
    [InlineData("no_inventory_handoff", "Post enabled")]
    public void GetPostingGateLabel_ReturnsExpectedOperatorLabel(string matchStatus, string expected)
    {
        var summary = CreateSummary(matchStatus);

        Assert.Equal(expected, BillReceiptPostingGate.GetPostingGateLabel(summary));
    }

    private static BillReceiptMatchingLaneSummary CreateSummary(string matchStatus) =>
        new(
            Guid.NewGuid(),
            1,
            10m,
            1,
            matchStatus == "partially_covered" ? 7.75m : 10m,
            matchStatus == "partially_covered" ? 2.25m : 0m,
            matchStatus,
            null,
            Array.Empty<BillReceiptMatchingReceiptSummary>(),
            Array.Empty<BillReceiptMatchingLineSummary>(),
            Array.Empty<BillReceiptMatchingDiscrepancySummary>());
}
