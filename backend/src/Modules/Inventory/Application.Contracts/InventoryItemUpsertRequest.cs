using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryItemUpsertRequest(
    Guid CompanyId,
    Guid UserId,
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
    Guid? DefaultPurchaseVarianceAccountId);
