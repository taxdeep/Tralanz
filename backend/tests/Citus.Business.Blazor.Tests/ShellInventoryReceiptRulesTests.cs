using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellInventoryReceiptRulesTests
{
    private static readonly Guid CompanyId = Guid.Parse("1b710446-1d32-42a0-8d7d-e53c0406a423");
    private static readonly Guid UserId = Guid.Parse("a6d72c8d-c392-4e4e-aee4-1aabcbf0f27c");
    private static readonly Guid VendorId = Guid.Parse("68bd4a24-6cfc-4e78-8756-331627980c1f");
    private static readonly Guid ItemId = Guid.Parse("ae7ec491-89a2-4237-a2c2-fab9759686d7");
    private static readonly Guid WarehouseId = Guid.Parse("a7a69d16-eb1d-4e66-81f6-7df2e0f264cb");

    [Fact]
    public void ValidatePost_Fails_WhenVendorMissing()
    {
        var result = ShellInventoryReceiptRules.ValidatePost(
            BuildRequest(vendorId: Guid.NewGuid()),
            BuildDashboard(),
            BuildCounterparties());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_vendor", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenCurrencyDisabledForCompany()
    {
        var counterparties = BuildCounterparties(enabledCurrencies:
            [
                new ShellCompanyCurrencyOption
                {
                    Code = "CAD",
                    Name = "Canadian Dollar"
                }
            ]);

        var result = ShellInventoryReceiptRules.ValidatePost(
            BuildRequest(currencyCode: "USD"),
            BuildDashboard(),
            counterparties);

        Assert.False(result.Succeeded);
        Assert.Equal("unsupported_currency", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenSingleCurrencyCompanyUsesForeignCurrency()
    {
        var counterparties = BuildCounterparties(multiCurrencyEnabled: false);

        var result = ShellInventoryReceiptRules.ValidatePost(
            BuildRequest(currencyCode: "USD"),
            BuildDashboard(),
            counterparties);

        Assert.False(result.Succeeded);
        Assert.Equal("base_currency_required", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenLineUsesUnknownWarehouse()
    {
        var result = ShellInventoryReceiptRules.ValidatePost(
            BuildRequest(lines:
            [
                new InventoryPurchaseReceiptLineInput(
                    1,
                    ItemId,
                    Guid.NewGuid(),
                    "EA",
                    5m,
                    10m,
                    null,
                    null)
            ]),
            BuildDashboard(),
            BuildCounterparties());

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_warehouse", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenLineUomDoesNotMatchStockUom()
    {
        var result = ShellInventoryReceiptRules.ValidatePost(
            BuildRequest(lines:
            [
                new InventoryPurchaseReceiptLineInput(
                    1,
                    ItemId,
                    WarehouseId,
                    "BOX",
                    5m,
                    10m,
                    null,
                    null)
            ]),
            BuildDashboard(),
            BuildCounterparties());

        Assert.False(result.Succeeded);
        Assert.Equal("uom_mismatch", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Succeeds_WhenContextIsValid()
    {
        var result = ShellInventoryReceiptRules.ValidatePost(
            BuildRequest(),
            BuildDashboard(),
            BuildCounterparties());

        Assert.True(result.Succeeded);
    }

    private static InventoryPurchaseReceiptPostRequest BuildRequest(
        Guid? vendorId = null,
        string currencyCode = "CAD",
        IReadOnlyList<InventoryPurchaseReceiptLineInput>? lines = null)
        => new(
            CompanyId,
            UserId,
            vendorId ?? VendorId,
            new DateOnly(2026, 4, 17),
            currencyCode,
            1m,
            null,
            null,
            null,
            "First receipt",
            lines ??
            [
                new InventoryPurchaseReceiptLineInput(
                    1,
                    ItemId,
                    WarehouseId,
                    "EA",
                    5m,
                    10m,
                    null,
                    null)
            ]);

    private static InventoryPurchaseReceiptDashboard BuildDashboard() =>
        new(
            CompanyId,
            "CAD",
            [
                new InventoryManagedItemSummary(
                    ItemId,
                    CompanyId,
                    "STK100",
                    "Inventory Widget",
                    null,
                    InventoryItemKind.Stock,
                    "EA",
                    ManageInventoryMethod.ManageStock,
                    InventoryCostingMethod.MovingAverage,
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

    private static ShellCounterpartyOnboardingSummary BuildCounterparties(
        bool multiCurrencyEnabled = true,
        IReadOnlyList<ShellCompanyCurrencyOption>? enabledCurrencies = null)
        => new()
        {
            BaseCurrencyCode = "CAD",
            MultiCurrencyEnabled = multiCurrencyEnabled,
            EnabledCurrencies = enabledCurrencies ??
            [
                new ShellCompanyCurrencyOption
                {
                    Code = "CAD",
                    Name = "Canadian Dollar"
                },
                new ShellCompanyCurrencyOption
                {
                    Code = "USD",
                    Name = "US Dollar"
                }
            ],
            Vendors =
            [
                new ShellManagedCounterpartySummary
                {
                    Id = VendorId,
                    EntityNumber = "V-001",
                    DisplayName = "Northwind Supply",
                    DefaultCurrencyCode = "CAD",
                    Email = "",
                    Phone = "",
                    Address = "",
                    IsActive = true
                }
            ]
        };
}
