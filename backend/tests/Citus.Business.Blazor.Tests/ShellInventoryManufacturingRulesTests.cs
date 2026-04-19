using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellInventoryManufacturingRulesTests
{
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _itemAId = Guid.NewGuid();
    private readonly Guid _itemBId = Guid.NewGuid();
    private readonly Guid _warehouseId = Guid.NewGuid();
    private readonly Guid _bomId = Guid.NewGuid();

    [Fact]
    public void ValidateBom_Fails_When_ComponentRepeatsOutput()
    {
        var dashboard = BuildDashboard();
        var request = new InventoryBomUpsertRequest(
            _companyId,
            Guid.NewGuid(),
            null,
            "BOM-001",
            _itemAId,
            1m,
            true,
            [new InventoryBomComponentInput(1, _itemAId, 1m, 0m, null)]);

        var result = ShellInventoryManufacturingRules.ValidateBom(request, dashboard);

        Assert.False(result.Succeeded);
        Assert.Equal("self_component", result.ErrorCode);
    }

    [Fact]
    public void ValidateBom_Fails_When_ComponentQuantityIsNotPositive()
    {
        var dashboard = BuildDashboard();
        var request = new InventoryBomUpsertRequest(
            _companyId,
            Guid.NewGuid(),
            null,
            "BOM-001",
            _itemAId,
            1m,
            true,
            [new InventoryBomComponentInput(1, _itemBId, 0m, 0m, null)]);

        var result = ShellInventoryManufacturingRules.ValidateBom(request, dashboard);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_component_qty", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_When_BomIsInactive()
    {
        var dashboard = BuildDashboard(isBomActive: false);
        var request = new InventoryManufacturingPostRequest(
            _companyId,
            Guid.NewGuid(),
            _bomId,
            _warehouseId,
            DateOnly.FromDateTime(DateTime.Today),
            1m,
            null);

        var result = ShellInventoryManufacturingRules.ValidatePost(request, dashboard);

        Assert.False(result.Succeeded);
        Assert.Equal("inactive_bom", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Succeeds_For_ActiveBomAndWarehouse()
    {
        var dashboard = BuildDashboard();
        var request = new InventoryManufacturingPostRequest(
            _companyId,
            Guid.NewGuid(),
            _bomId,
            _warehouseId,
            DateOnly.FromDateTime(DateTime.Today),
            2m,
            "Build two units");

        var result = ShellInventoryManufacturingRules.ValidatePost(request, dashboard);

        Assert.True(result.Succeeded);
    }

    private InventoryManufacturingDashboard BuildDashboard(bool isBomActive = true)
    {
        var outputItem = new InventoryManagedItemSummary(
            _itemAId,
            _companyId,
            "FG-100",
            "Finished Good",
            null,
            InventoryItemKind.Stock,
            "EA",
            ManageInventoryMethod.ManageStock,
            InventoryCostingMethod.Fifo,
            InventoryBackorderMode.Disallow,
            InventoryLowStockActivity.Nothing,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow);
        var componentItem = new InventoryManagedItemSummary(
            _itemBId,
            _companyId,
            "RM-100",
            "Raw Material",
            null,
            InventoryItemKind.Stock,
            "EA",
            ManageInventoryMethod.ManageStock,
            InventoryCostingMethod.Fifo,
            InventoryBackorderMode.Disallow,
            InventoryLowStockActivity.Nothing,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow);
        var warehouse = new InventoryManagedWarehouseSummary(
            _warehouseId,
            _companyId,
            "MAIN",
            "Main Warehouse",
            null,
            true,
            DateTimeOffset.UtcNow);
        var bom = new InventoryBomSummary(
            _bomId,
            _companyId,
            "BOM-001",
            _itemAId,
            "FG-100",
            "Finished Good",
            "EA",
            1m,
            isBomActive,
            DateTimeOffset.UtcNow,
            new InventoryBomCostRollupSummary(10m, 10m, true, null),
            [new InventoryBomComponentInput(1, _itemBId, 1m, 0m, null)]);

        return new InventoryManufacturingDashboard(
            _companyId,
            "CAD",
            [outputItem, componentItem],
            [warehouse],
            [bom],
            []);
    }
}
