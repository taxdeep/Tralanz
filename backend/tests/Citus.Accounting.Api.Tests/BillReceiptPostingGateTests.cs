using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class BillReceiptPostingGateTests
{
    [Theory]
    [InlineData("no_inventory_handoff", true)]
    [InlineData("fully_receipted", true)]
    [InlineData("no_receipt", false)]
    [InlineData("partially_receipted", false)]
    [InlineData("over_receipted", false)]
    [InlineData("unexpected", false)]
    public void AllowsBillPost_MatchesReceiptFirstAuthority(string matchStatus, bool expected)
    {
        Assert.Equal(expected, BillReceiptPostingGate.AllowsBillPost(matchStatus));
    }

    [Fact]
    public void GetBlockedPostMessage_UsesRemainingQuantity_ForPartialReceipt()
    {
        var summary = new InventoryBillReceiptHandoffSummary(
            Guid.NewGuid(),
            1,
            12m,
            1,
            4.5m,
            7.5m,
            "partially_receipted",
            null,
            Array.Empty<InventoryPurchaseReceiptSummary>(),
            Array.Empty<InventoryBillReceiptHandoffLineSummary>());

        var message = BillReceiptPostingGate.GetBlockedPostMessage(summary);

        Assert.Contains("7.50", message);
        Assert.Contains("outstanding", message);
    }

    [Theory]
    [InlineData("no_receipt", "Post on hold: no receipt yet")]
    [InlineData("partially_receipted", "Post on hold: receipt still partial")]
    [InlineData("over_receipted", "Post on hold: receipt mismatch")]
    [InlineData("fully_receipted", "Post enabled")]
    [InlineData("no_inventory_handoff", "Post enabled")]
    public void GetPostingGateLabel_ReturnsExpectedOperatorLabel(string matchStatus, string expected)
    {
        var summary = CreateSummary(matchStatus);

        Assert.Equal(expected, BillReceiptPostingGate.GetPostingGateLabel(summary));
    }

    private static InventoryBillReceiptHandoffSummary CreateSummary(string matchStatus) =>
        new(
            Guid.NewGuid(),
            1,
            10m,
            1,
            matchStatus == "partially_receipted" ? 7.75m : 10m,
            matchStatus == "partially_receipted" ? 2.25m : 0m,
            matchStatus,
            null,
            Array.Empty<InventoryPurchaseReceiptSummary>(),
            Array.Empty<InventoryBillReceiptHandoffLineSummary>());
}
