using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryItemUpsertRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? ItemId,
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
    Guid? DefaultSalesRevenueAccountId,
    Guid? DefaultDropShipClearingAccountId,
    decimal? DefaultSalesPrice,
    decimal? DefaultPurchasePrice,
    Guid? DefaultSalesTaxCodeId,
    Guid? DefaultPurchaseTaxCodeId);
