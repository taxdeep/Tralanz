using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellInventoryFoundationRulesTests
{
    private static readonly Guid CompanyId = Guid.Parse("8bbf87fc-fc5e-4ca6-b806-5b03074d9a66");
    private static readonly Guid UserId = Guid.Parse("6bc7819b-ac9d-4bdb-afd0-a7d5d9ccb0e4");
    private static readonly Guid InventoryAssetAccountId = Guid.Parse("32b56c05-bc74-44dd-a2ca-8cfc1ccdad69");
    private static readonly Guid CogsAccountId = Guid.Parse("5544e489-2265-4984-bfe4-ec8b2c0066c3");
    private static readonly Guid WriteOffAccountId = Guid.Parse("78b2c9e7-b23c-479b-91b2-f8487af2a0b3");
    private static readonly Guid PurchaseVarianceAccountId = Guid.Parse("8614ffd7-2fc4-4ad1-871a-a65fbd26f59b");

    [Fact]
    public void ValidateItemSave_Fails_WhenCodeMissing()
    {
        var result = ShellInventoryFoundationRules.ValidateItemSave(
            new InventoryItemUpsertRequest(
                CompanyId,
                UserId,
                null,
                "",
                "Inventory Widget",
                null,
                InventoryItemKind.Stock,
                "EA",
                ManageInventoryMethod.ManageStock,
                InventoryCostingMethod.MovingAverage,
                InventoryBackorderMode.Disallow,
                InventoryLowStockActivity.Warn,
                InventoryAssetAccountId,
                CogsAccountId,
                WriteOffAccountId,
                PurchaseVarianceAccountId),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_item_code", result.ErrorCode);
    }

    [Fact]
    public void ValidateItemSave_Fails_WhenServiceAttemptsStockTracking()
    {
        var result = ShellInventoryFoundationRules.ValidateItemSave(
            new InventoryItemUpsertRequest(
                CompanyId,
                UserId,
                null,
                "SVC100",
                "Tax Planning",
                null,
                InventoryItemKind.Service,
                null,
                ManageInventoryMethod.ManageStock,
                InventoryCostingMethod.MovingAverage,
                InventoryBackorderMode.Disallow,
                InventoryLowStockActivity.Warn,
                null,
                null,
                null,
                null),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("non_stock_tracking_forbidden", result.ErrorCode);
    }

    [Fact]
    public void ValidateItemSave_Fails_WhenCompanyScopedCodeAlreadyExists()
    {
        var dashboard = BuildDashboard(
        [
            new InventoryManagedItemSummary(
                Guid.Parse("fc0e2ef6-c1ea-4cea-9544-f9df17ca8996"),
                CompanyId,
                "INV100",
                "Existing Inventory Item",
                null,
                InventoryItemKind.Stock,
                "EA",
                ManageInventoryMethod.ManageStock,
                InventoryCostingMethod.MovingAverage,
                InventoryBackorderMode.Disallow,
                InventoryLowStockActivity.Warn,
                InventoryAssetAccountId,
                "1200 - Inventory",
                CogsAccountId,
                "5100 - COGS",
                WriteOffAccountId,
                "5299 - Write-Off",
                PurchaseVarianceAccountId,
                "5199 - Purchase Variance",
                true,
                DateTimeOffset.UtcNow)
        ]);

        var result = ShellInventoryFoundationRules.ValidateItemSave(
            new InventoryItemUpsertRequest(
                CompanyId,
                UserId,
                null,
                "inv100",
                "New Item",
                null,
                InventoryItemKind.Stock,
                "EA",
                ManageInventoryMethod.ManageStock,
                InventoryCostingMethod.MovingAverage,
                InventoryBackorderMode.Disallow,
                InventoryLowStockActivity.Warn,
                InventoryAssetAccountId,
                CogsAccountId,
                WriteOffAccountId,
                PurchaseVarianceAccountId),
            dashboard);

        Assert.False(result.Succeeded);
        Assert.Equal("duplicate_item_code", result.ErrorCode);
    }

    [Fact]
    public void ValidateItemSave_Fails_WhenStockUomMissing()
    {
        var result = ShellInventoryFoundationRules.ValidateItemSave(
            new InventoryItemUpsertRequest(
                CompanyId,
                UserId,
                null,
                "INV200",
                "Inventory Widget",
                null,
                InventoryItemKind.Stock,
                null,
                ManageInventoryMethod.ManageStock,
                InventoryCostingMethod.MovingAverage,
                InventoryBackorderMode.Disallow,
                InventoryLowStockActivity.Warn,
                InventoryAssetAccountId,
                CogsAccountId,
                WriteOffAccountId,
                PurchaseVarianceAccountId),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_stock_uom", result.ErrorCode);
    }

    [Fact]
    public void ValidateItemSave_Fails_WhenNewTrackedModeIsEnabled()
    {
        var result = ShellInventoryFoundationRules.ValidateItemSave(
            new InventoryItemUpsertRequest(
                CompanyId,
                UserId,
                null,
                "INV-TRACK-1",
                "Tracked Widget",
                null,
                InventoryItemKind.Stock,
                "EA",
                ManageInventoryMethod.ManageStockBySku,
                InventoryCostingMethod.MovingAverage,
                InventoryBackorderMode.Disallow,
                InventoryLowStockActivity.Warn,
                InventoryAssetAccountId,
                CogsAccountId,
                WriteOffAccountId,
                PurchaseVarianceAccountId),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("tracked_mode_guarded", result.ErrorCode);
    }

    [Fact]
    public void ValidateItemSave_AllowsExistingTrackedItemToRemainTracked()
    {
        var trackedItemId = Guid.Parse("738da5e8-00a3-4c0d-a20a-049d6317fd83");
        var dashboard = BuildDashboard(
        [
            new InventoryManagedItemSummary(
                trackedItemId,
                CompanyId,
                "INV-TRACK-1",
                "Tracked Widget",
                null,
                InventoryItemKind.Stock,
                "EA",
                ManageInventoryMethod.ManageStockBySku,
                InventoryCostingMethod.MovingAverage,
                InventoryBackorderMode.Disallow,
                InventoryLowStockActivity.Warn,
                InventoryAssetAccountId,
                "1200 - Inventory",
                CogsAccountId,
                "5100 - COGS",
                WriteOffAccountId,
                "5299 - Write-Off",
                PurchaseVarianceAccountId,
                "5199 - Purchase Variance",
                true,
                DateTimeOffset.UtcNow)
        ]);

        var result = ShellInventoryFoundationRules.ValidateItemSave(
            new InventoryItemUpsertRequest(
                CompanyId,
                UserId,
                trackedItemId,
                "INV-TRACK-1",
                "Tracked Widget",
                "Legacy tracked item kept under guardrail.",
                InventoryItemKind.Stock,
                "EA",
                ManageInventoryMethod.ManageStockBySku,
                InventoryCostingMethod.MovingAverage,
                InventoryBackorderMode.Disallow,
                InventoryLowStockActivity.Warn,
                InventoryAssetAccountId,
                CogsAccountId,
                WriteOffAccountId,
                PurchaseVarianceAccountId),
            dashboard);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateItemSave_Fails_WhenInventoryAssetAccountMissing()
    {
        var result = ShellInventoryFoundationRules.ValidateItemSave(
            new InventoryItemUpsertRequest(
                CompanyId,
                UserId,
                null,
                "INV201",
                "Inventory Widget",
                null,
                InventoryItemKind.Stock,
                "EA",
                ManageInventoryMethod.ManageStock,
                InventoryCostingMethod.MovingAverage,
                InventoryBackorderMode.Disallow,
                InventoryLowStockActivity.Warn,
                null,
                CogsAccountId,
                WriteOffAccountId,
                PurchaseVarianceAccountId),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_inventory_asset_account", result.ErrorCode);
    }

    [Fact]
    public void ValidateWarehouseSave_Fails_WhenNameAlreadyExists()
    {
        var dashboard = BuildDashboard(
            warehouses:
            [
                new InventoryManagedWarehouseSummary(
                    Guid.Parse("0f3de0a5-c4dc-4f1c-9a6b-061d0f0cf9c9"),
                    CompanyId,
                    "MAIN",
                    "Main Warehouse",
                    null,
                    true,
                    DateTimeOffset.UtcNow)
            ]);

        var result = ShellInventoryFoundationRules.ValidateWarehouseSave(
            new InventoryWarehouseUpsertRequest(
                CompanyId,
                UserId,
                null,
                "WEST",
                "Main Warehouse",
                null),
            dashboard);

        Assert.False(result.Succeeded);
        Assert.Equal("duplicate_warehouse_name", result.ErrorCode);
    }

    [Fact]
    public void ValidateWarehouseActiveStateChange_Fails_WhenLastActiveWarehouseWouldBeDeactivated()
    {
        var warehouse = new InventoryManagedWarehouseSummary(
            Guid.Parse("12603df1-9169-4615-a89b-24344817b673"),
            CompanyId,
            "MAIN",
            "Main Warehouse",
            null,
            true,
            DateTimeOffset.UtcNow);

        var dashboard = BuildDashboard(warehouses: [warehouse]);

        var result = ShellInventoryFoundationRules.ValidateWarehouseActiveStateChange(warehouse, false, dashboard);

        Assert.False(result.Succeeded);
        Assert.Equal("last_active_warehouse_protected", result.ErrorCode);
    }

    private static InventoryFoundationDashboard BuildDashboard(
        IReadOnlyList<InventoryManagedItemSummary>? items = null,
        IReadOnlyList<InventoryManagedWarehouseSummary>? warehouses = null)
    {
        var summary = new InventoryFoundationSummary(
            CompanyId,
            new InventoryCostingPolicyRecord(
                CompanyId,
                InventoryCostingMethod.MovingAverage,
                false,
                true,
                UserId,
                DateTimeOffset.UtcNow,
                null,
                DateTimeOffset.UtcNow),
            items?.Count ?? 0,
            warehouses?.Count ?? 0,
            warehouses?.Count(static item => item.IsActive) ?? 0,
            0,
            0,
            0);

        return new InventoryFoundationDashboard(
            summary,
            items ?? [],
            warehouses ?? [],
            [
                new InventoryFoundationAccountOption(
                    InventoryAssetAccountId,
                    "1200",
                    "Inventory",
                    "asset",
                    "inventory_asset",
                    "CAD")
            ],
            [
                new InventoryFoundationAccountOption(
                    CogsAccountId,
                    "5100",
                    "COGS",
                    "cost_of_sales",
                    "cost_of_sales",
                    "CAD"),
                new InventoryFoundationAccountOption(
                    WriteOffAccountId,
                    "5299",
                    "Inventory Write-Off",
                    "expense",
                    "expense",
                    "CAD"),
                new InventoryFoundationAccountOption(
                    PurchaseVarianceAccountId,
                    "5199",
                    "Purchase Variance",
                    "expense",
                    "expense",
                    "CAD")
            ]);
    }
}
