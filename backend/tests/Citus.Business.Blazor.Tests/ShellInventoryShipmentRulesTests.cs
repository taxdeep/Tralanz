using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellInventoryShipmentRulesTests
{
    private static readonly Guid CompanyId = Guid.Parse("3d0625bc-4679-4c18-8d07-7938baa6fac0");
    private static readonly Guid UserId = Guid.Parse("789a3554-6a33-42ac-9fab-82b0925cbc66");
    private static readonly Guid CustomerId = Guid.Parse("23f1d0b0-3aa2-405b-bd18-6da2f5e17bf0");
    private static readonly Guid ItemId = Guid.Parse("7a28fd13-84d9-4d29-9e31-cd976a39ef91");
    private static readonly Guid WarehouseAId = Guid.Parse("4e2b6acd-5d4d-4dbd-9f01-f4c3cdc2d788");
    private static readonly Guid WarehouseBId = Guid.Parse("5c662454-c3b6-4c44-9ab0-24f694629d7d");

    [Fact]
    public void ValidatePost_Fails_WhenCustomerMissing()
    {
        var result = ShellInventoryShipmentRules.ValidatePost(
            BuildRequest(customerId: Guid.NewGuid()),
            BuildDashboard(),
            BuildCounterparties());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_customer", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenUomDoesNotMatchStockUom()
    {
        var result = ShellInventoryShipmentRules.ValidatePost(
            BuildRequest(lines:
            [
                new InventoryShipmentLineInput(
                    1,
                    ItemId,
                    WarehouseAId,
                    "BOX",
                    2m,
                    null,
                    null)
            ]),
            BuildDashboard(),
            BuildCounterparties());

        Assert.False(result.Succeeded);
        Assert.Equal("uom_mismatch", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Succeeds_ForSplitWarehouseShipment()
    {
        var result = ShellInventoryShipmentRules.ValidatePost(
            BuildRequest(lines:
            [
                new InventoryShipmentLineInput(1, ItemId, WarehouseAId, "EA", 2m, null, null),
                new InventoryShipmentLineInput(2, ItemId, WarehouseBId, "EA", 3m, null, null)
            ]),
            BuildDashboard(),
            BuildCounterparties());

        Assert.True(result.Succeeded);
    }

    private static InventoryShipmentPostRequest BuildRequest(
        Guid? customerId = null,
        IReadOnlyList<InventoryShipmentLineInput>? lines = null)
        => new(
            CompanyId,
            UserId,
            customerId ?? CustomerId,
            new DateOnly(2026, 4, 18),
            "Canada Post",
            "TRACK-123",
            "SLIP-123",
            "ar_invoice",
            Guid.Parse("837b1cca-4f35-4487-9932-93a5053b5947"),
            "INV-000123",
            "First shipment",
            lines ??
            [
                new InventoryShipmentLineInput(
                    1,
                    ItemId,
                    WarehouseAId,
                    "EA",
                    2m,
                    null,
                    null)
            ]);

    private static InventoryShipmentDashboard BuildDashboard() =>
        new(
            CompanyId,
            "CAD",
            [
                new InventoryManagedItemSummary(
                    ItemId,
                    CompanyId,
                    "STK200",
                    "Finished Widget",
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
                    WarehouseAId,
                    CompanyId,
                    "MAIN",
                    "Main Warehouse",
                    null,
                    true,
                    DateTimeOffset.UtcNow),
                new InventoryManagedWarehouseSummary(
                    WarehouseBId,
                    CompanyId,
                    "EAST",
                    "East Warehouse",
                    null,
                    true,
                    DateTimeOffset.UtcNow)
            ],
            []);

    private static ShellCounterpartyOnboardingSummary BuildCounterparties() =>
        new()
        {
            BaseCurrencyCode = "CAD",
            MultiCurrencyEnabled = false,
            Customers =
            [
                new ShellManagedCounterpartySummary
                {
                    Id = CustomerId,
                    EntityNumber = "C-001",
                    DisplayName = "Blue Harbor Retail",
                    DefaultCurrencyCode = "CAD",
                    Email = "",
                    Phone = "",
                    Address = "",
                    IsActive = true
                }
            ]
        };
}
