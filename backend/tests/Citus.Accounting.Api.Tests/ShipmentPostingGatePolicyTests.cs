using Citus.Accounting.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class ShipmentPostingGatePolicyTests
{
    [Theory]
    [InlineData("no_inventory_handoff", true)]
    [InlineData("fully_shipped", true)]
    [InlineData("no_shipment", false)]
    [InlineData("partially_shipped", false)]
    [InlineData("over_shipped", false)]
    [InlineData("unexpected", false)]
    public void AllowsInvoicePost_MatchesShipmentFirstAuthority(string matchStatus, bool expected)
    {
        Assert.Equal(expected, ShipmentPostingGatePolicy.AllowsInvoicePost(matchStatus));
    }

    [Fact]
    public void GetBlockedPostMessage_UsesRemainingQuantity_ForPartialShipment()
    {
        var summary = new InventoryInvoiceShipmentHandoffSummary(
            Guid.NewGuid(),
            1,
            12m,
            1,
            4.5m,
            7.5m,
            "partially_shipped",
            null,
            Array.Empty<InventoryShipmentSummary>(),
            Array.Empty<InventoryInvoiceShipmentHandoffLineSummary>());

        var message = ShipmentPostingGatePolicy.GetBlockedPostMessage(summary);

        Assert.Contains("7.50", message);
        Assert.Contains("outstanding", message);
    }

    [Theory]
    [InlineData("no_shipment", "Post on hold: no shipment yet")]
    [InlineData("partially_shipped", "Post on hold: shipment still partial")]
    [InlineData("over_shipped", "Post on hold: shipment mismatch")]
    [InlineData("fully_shipped", "Post enabled")]
    [InlineData("no_inventory_handoff", "Post enabled")]
    public void GetPostingGateLabel_ReturnsExpectedOperatorLabel(string matchStatus, string expected)
    {
        var summary = CreateSummary(matchStatus);

        Assert.Equal(expected, ShipmentPostingGatePolicy.GetPostingGateLabel(summary));
    }

    private static InventoryInvoiceShipmentHandoffSummary CreateSummary(string matchStatus) =>
        new(
            Guid.NewGuid(),
            1,
            10m,
            1,
            matchStatus == "partially_shipped" ? 7.75m : 10m,
            matchStatus == "partially_shipped" ? 2.25m : 0m,
            matchStatus,
            null,
            Array.Empty<InventoryShipmentSummary>(),
            Array.Empty<InventoryInvoiceShipmentHandoffLineSummary>());
}
