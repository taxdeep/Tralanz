using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellInventoryTransferRulesTests
{
    private static readonly Guid CompanyId = Guid.Parse("4b2efca4-1d55-4b7c-a878-9f257ec03292");
    private static readonly Guid UserId = Guid.Parse("f8a79e01-3607-4a4b-93e2-6ba9640c7275");
    private static readonly Guid ItemId = Guid.Parse("d01f5df5-ef8f-4fbe-a66e-2f1d652858e9");
    private static readonly Guid SourceWarehouseId = Guid.Parse("f847ad42-c275-4df1-b9f6-d554f60ad56e");
    private static readonly Guid DestinationWarehouseId = Guid.Parse("d4830cdb-31a9-4696-b5ea-a98ae8ded8f9");

    [Fact]
    public void ValidateUpsert_Fails_WhenWarehousesMatch()
    {
        var result = ShellInventoryTransferRules.ValidateUpsert(
            BuildRequest(sourceWarehouseId: SourceWarehouseId, destinationWarehouseId: SourceWarehouseId),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("same_warehouse", result.ErrorCode);
    }

    [Fact]
    public void ValidateUpsert_Fails_WhenUomDoesNotMatchStockUom()
    {
        var result = ShellInventoryTransferRules.ValidateUpsert(
            BuildRequest(lines:
            [
                new InventoryTransferLineInput(1, ItemId, "BOX", 2m, null)
            ]),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("uom_mismatch", result.ErrorCode);
    }

    [Fact]
    public void ValidateUpsert_Fails_WhenDestinationWarehouseMissing()
    {
        var result = ShellInventoryTransferRules.ValidateUpsert(
            BuildRequest(destinationWarehouseId: Guid.NewGuid()),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_warehouse", result.ErrorCode);
    }

    [Fact]
    public void ValidateUpsert_Succeeds_WhenContextIsValid()
    {
        var result = ShellInventoryTransferRules.ValidateUpsert(BuildRequest(), BuildDashboard());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateSubmit_Fails_WhenTransferIsMissing()
    {
        var result = ShellInventoryTransferRules.ValidateSubmit(CompanyId, Guid.NewGuid(), BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_transfer", result.ErrorCode);
    }

    [Fact]
    public void ValidateShip_Fails_WhenTransferIsNotSubmitted()
    {
        var transfer = BuildTransferSummary(status: "draft");

        var result = ShellInventoryTransferRules.ValidateShip(
            CompanyId,
            transfer.TransferId,
            DateOnly.FromDateTime(DateTime.Today),
            BuildDashboard(recentTransfers: [transfer]));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_ship_status", result.ErrorCode);
    }

    [Fact]
    public void ValidateReceive_Fails_WhenDateIsEarlierThanShipDate()
    {
        var shipStamp = DateTimeOffset.UtcNow;
        var transfer = BuildTransferSummary(status: "shipped", shippedAt: shipStamp);

        var result = ShellInventoryTransferRules.ValidateReceive(
            CompanyId,
            transfer.TransferId,
            DateOnly.FromDateTime(shipStamp.UtcDateTime.Date.AddDays(-1)),
            BuildDashboard(recentTransfers: [transfer]));

        Assert.False(result.Succeeded);
        Assert.Equal("receive_before_ship", result.ErrorCode);
    }

    [Fact]
    public void ValidateReceive_Succeeds_WhenShippedTransferDateIsValid()
    {
        var shipStamp = DateTimeOffset.UtcNow;
        var transfer = BuildTransferSummary(status: "shipped", shippedAt: shipStamp);

        var result = ShellInventoryTransferRules.ValidateReceive(
            CompanyId,
            transfer.TransferId,
            DateOnly.FromDateTime(shipStamp.UtcDateTime.Date),
            BuildDashboard(recentTransfers: [transfer]));

        Assert.True(result.Succeeded);
    }

    private static InventoryTransferUpsertRequest BuildRequest(
        Guid? sourceWarehouseId = null,
        Guid? destinationWarehouseId = null,
        IReadOnlyList<InventoryTransferLineInput>? lines = null) =>
        new(
            CompanyId,
            UserId,
            null,
            sourceWarehouseId ?? SourceWarehouseId,
            destinationWarehouseId ?? DestinationWarehouseId,
            "First transfer",
            lines ??
            [
                new InventoryTransferLineInput(1, ItemId, "EA", 2m, null)
            ]);

    private static InventoryTransferDashboard BuildDashboard(
        IReadOnlyList<InventoryTransferSummary>? recentTransfers = null) =>
        new(
            CompanyId,
            "CAD",
            [
                new InventoryManagedItemSummary(
                    ItemId,
                    CompanyId,
                    "STK100",
                    "Transfer Item",
                    null,
                    InventoryItemKind.Stock,
                    "EA",
                    ManageInventoryMethod.ManageStock,
                    InventoryCostingMethod.Fifo,
                    InventoryBackorderMode.Disallow,
                    InventoryLowStockActivity.Warn,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    true,
                    DateTimeOffset.UtcNow)
            ],
            [
                new InventoryManagedWarehouseSummary(
                    SourceWarehouseId,
                    CompanyId,
                    "MAIN",
                    "Main Warehouse",
                    null,
                    true,
                    DateTimeOffset.UtcNow),
                new InventoryManagedWarehouseSummary(
                    DestinationWarehouseId,
                    CompanyId,
                    "EAST",
                    "East Warehouse",
                    null,
                    true,
                    DateTimeOffset.UtcNow)
            ],
            recentTransfers ?? []);

    private static InventoryTransferSummary BuildTransferSummary(
        string status,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? shippedAt = null,
        DateTimeOffset? receivedAt = null) =>
        new(
            Guid.NewGuid(),
            CompanyId,
            "XFER-001",
            status,
            SourceWarehouseId,
            "MAIN",
            "Main Warehouse",
            DestinationWarehouseId,
            "EAST",
            "East Warehouse",
            2m,
            1,
            DateTimeOffset.UtcNow.AddMinutes(-15),
            submittedAt,
            shippedAt,
            receivedAt,
            "First transfer");
}
