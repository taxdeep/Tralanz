namespace Citus.Modules.Inventory.Domain.Shared;

public sealed record class InventoryItemRecord(
    Guid Id,
    CompanyId CompanyId,
    string ItemCode,
    string Name,
    string Description,
    InventoryItemKind ItemKind,
    string StockUomCode,
    ManageInventoryMethod ManageInventoryMethod,
    InventoryCostingMethod CostingMethod,
    InventoryBackorderMode BackorderMode,
    InventoryLowStockActivity LowStockActivity,
    Guid? DefaultInventoryAssetAccountId,
    Guid? DefaultCogsAccountId,
    Guid? DefaultWriteOffAccountId,
    Guid? DefaultPurchaseVarianceAccountId,
    Guid? DefaultSalesRevenueAccountId,
    Guid? DefaultDropShipClearingAccountId,
    Guid? DefaultSalesTaxCodeId,
    Guid? DefaultPurchaseTaxCodeId,
    bool IsActive);
