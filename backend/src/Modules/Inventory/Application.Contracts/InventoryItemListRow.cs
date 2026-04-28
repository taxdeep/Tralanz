using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

/// <summary>
/// Item-list projection used by the Items / Services Blazor page. Carries the
/// full set of fields the form edits — pricing, tax-code defaults, accounting
/// defaults, inventory tracking — so the page can render rows and pre-fill
/// the edit form without a second round-trip.
/// </summary>
public sealed record class InventoryItemListRow(
    Guid Id,
    Guid CompanyId,
    string ItemCode,
    string Name,
    string? Description,
    InventoryItemKind ItemKind,
    string? StockUomCode,
    ManageInventoryMethod ManageInventoryMethod,
    InventoryCostingMethod DefaultCostingMethod,
    InventoryBackorderMode BackorderMode,
    InventoryLowStockActivity LowStockActivity,
    Guid? DefaultInventoryAssetAccountId,
    Guid? DefaultCogsAccountId,
    Guid? DefaultWriteOffAccountId,
    Guid? DefaultPurchaseVarianceAccountId,
    decimal? DefaultSalesPrice,
    decimal? DefaultPurchasePrice,
    Guid? DefaultSalesTaxCodeId,
    Guid? DefaultPurchaseTaxCodeId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
