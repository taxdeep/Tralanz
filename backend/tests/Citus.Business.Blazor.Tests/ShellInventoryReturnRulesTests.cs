using Citus.Modules.Inventory.Application.Contracts;
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellInventoryReturnRulesTests
{
    private static readonly Guid CompanyId = Guid.Parse("0f955be7-5530-45f5-a68a-79c8227f6f2f");
    private static readonly Guid UserId = Guid.Parse("d3d6278f-1eff-469d-bde0-c6e8c4d47423");
    private static readonly Guid CustomerId = Guid.Parse("bc9ce0de-e3d7-4136-bd6f-cb95383fa66c");
    private static readonly Guid ShipmentId = Guid.Parse("804e5be2-fdca-4a5b-bcb0-b6b5af5faef4");
    private static readonly Guid ItemId = Guid.Parse("60be790a-af87-4448-bd8a-e6cc27c4c664");
    private static readonly Guid WarehouseId = Guid.Parse("e5782721-441d-4803-9b24-a46f1f1fab0f");

    [Fact]
    public void ValidatePost_Fails_WhenShipmentAnchorMissing()
    {
        var result = ShellInventoryReturnRules.ValidatePost(
            BuildRequest(),
            null);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_handoff", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenQuantityExceedsRemainingReturnable()
    {
        var result = ShellInventoryReturnRules.ValidatePost(
            BuildRequest(lines:
            [
                new InventoryReturnReceiveLineInput(
                    1,
                    ItemId,
                    WarehouseId,
                    "EA",
                    7m,
                    "SELLABLE",
                    "DAMAGED_ON_DELIVERY",
                    null,
                    null)
            ]),
            BuildHandoffSummary());

        Assert.False(result.Succeeded);
        Assert.Equal("quantity_ceiling", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenConditionMissing()
    {
        var result = ShellInventoryReturnRules.ValidatePost(
            BuildRequest(lines:
            [
                new InventoryReturnReceiveLineInput(
                    1,
                    ItemId,
                    WarehouseId,
                    "EA",
                    2m,
                    "",
                    "DAMAGED_ON_DELIVERY",
                    null,
                    null)
            ]),
            BuildHandoffSummary());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_condition", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Succeeds_WhenRequestFitsShipmentAnchor()
    {
        var result = ShellInventoryReturnRules.ValidatePost(
            BuildRequest(),
            BuildHandoffSummary());

        Assert.True(result.Succeeded);
    }

    private static InventoryReturnReceivePostRequest BuildRequest(
        IReadOnlyList<InventoryReturnReceiveLineInput>? lines = null)
        => new(
            CompanyId,
            UserId,
            CustomerId,
            new DateOnly(2026, 4, 19),
            ShipmentId,
            "SHP-20260418-AAAA1111",
            "Customer sent inventory back for review.",
            lines ??
            [
                new InventoryReturnReceiveLineInput(
                    1,
                    ItemId,
                    WarehouseId,
                    "EA",
                    2m,
                    "SELLABLE",
                    "DAMAGED_ON_DELIVERY",
                    null,
                    null)
            ]);

    private static InventoryReturnReceiveHandoffSummary BuildHandoffSummary() =>
        new(
            ShipmentId,
            "SHP-20260418-AAAA1111",
            CustomerId,
            "Northwind Retail",
            new DateOnly(2026, 4, 18),
            1,
            10m,
            1,
            4m,
            6m,
            "partially_returned",
            DateTimeOffset.UtcNow.AddDays(-1),
            [],
            [
                new InventoryReturnReceiveHandoffLineSummary(
                    ItemId,
                    "STK100",
                    "Inventory Widget",
                    WarehouseId,
                    "MAIN",
                    "Main Warehouse",
                    "EA",
                    10m,
                    4m,
                    6m,
                    "partially_returned")
            ]);
}
