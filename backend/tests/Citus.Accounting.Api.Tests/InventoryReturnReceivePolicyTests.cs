using Citus.Modules.Inventory.Application;

namespace Citus.Accounting.Api.Tests;

public sealed class InventoryReturnReceivePolicyTests
{
    [Fact]
    public void ResolveMatchStatus_ReturnsNoShipment_WhenAnchorMissing()
    {
        Assert.Equal(
            "no_shipment",
            InventoryReturnReceivePolicy.ResolveMatchStatus(0, 0m, 0m));
    }

    [Fact]
    public void ResolveMatchStatus_ReturnsPartiallyReturned_WhenNotFullyCovered()
    {
        Assert.Equal(
            "partially_returned",
            InventoryReturnReceivePolicy.ResolveMatchStatus(2, 10m, 4m));
    }

    [Fact]
    public void ResolveMatchStatus_ReturnsFullyReturned_WhenExact()
    {
        Assert.Equal(
            "fully_returned",
            InventoryReturnReceivePolicy.ResolveMatchStatus(2, 10m, 10m));
    }

    [Fact]
    public void ResolveMatchStatus_ReturnsOverReturned_WhenExceeded()
    {
        Assert.Equal(
            "over_returned",
            InventoryReturnReceivePolicy.ResolveMatchStatus(2, 10m, 11m));
    }

    [Fact]
    public void ResolveRemainingReturnableQuantity_SubtractsReturnedQuantity()
    {
        Assert.Equal(
            6m,
            InventoryReturnReceivePolicy.ResolveRemainingReturnableQuantity(10m, 4m));
    }
}
