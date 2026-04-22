using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellInventoryIssueRulesTests
{
    private static readonly Guid CompanyId = Guid.Parse("0d8ef11e-1d54-4b90-8d40-531f7d8b6fb0");
    private static readonly Guid UserId = Guid.Parse("72a4f39d-ff76-49e5-ab11-ec0fd93520cf");
    private static readonly Guid CustomerId = Guid.Parse("646657a3-ae7a-47a4-a619-41ae87b63b89");
    private static readonly Guid ItemId = Guid.Parse("180424a0-4c6a-4d8d-a766-d2244ca7478d");
    private static readonly Guid WarehouseId = Guid.Parse("b58dde0e-f1db-4ffd-ad78-ea5c5be9646c");

    [Fact]
    public void ValidatePost_Fails_WhenCustomerMissing()
    {
        var result = ShellInventoryIssueRules.ValidatePost(
            BuildRequest(customerId: Guid.NewGuid()),
            BuildDashboard(),
            BuildCounterparties());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_customer", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenUomDoesNotMatchStockUom()
    {
        var result = ShellInventoryIssueRules.ValidatePost(
            BuildRequest(lines:
            [
                new InventorySalesIssueLineInput(
                    1,
                    ItemId,
                    WarehouseId,
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
    public void ValidatePost_Fails_WhenWarehouseMissing()
    {
        var result = ShellInventoryIssueRules.ValidatePost(
            BuildRequest(lines:
            [
                new InventorySalesIssueLineInput(
                    1,
                    ItemId,
                    Guid.NewGuid(),
                    "EA",
                    2m,
                    null,
                    null)
            ]),
            BuildDashboard(),
            BuildCounterparties());

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_warehouse", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Succeeds_WhenContextIsValid()
    {
        var result = ShellInventoryIssueRules.ValidatePost(
            BuildRequest(),
            BuildDashboard(),
            BuildCounterparties());

        Assert.True(result.Succeeded);
    }

    private static InventorySalesIssuePostRequest BuildRequest(
        Guid? customerId = null,
        IReadOnlyList<InventorySalesIssueLineInput>? lines = null)
        => new(
            CompanyId,
            UserId,
            customerId ?? CustomerId,
            new DateOnly(2026, 4, 17),
            null,
            null,
            null,
            "First issue",
            lines ??
            [
                new InventorySalesIssueLineInput(
                    1,
                    ItemId,
                    WarehouseId,
                    "EA",
                    2m,
                    null,
                    null)
            ]);

    private static InventorySalesIssueDashboard BuildDashboard() =>
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
                    WarehouseId,
                    CompanyId,
                    "MAIN",
                    "Main Warehouse",
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
