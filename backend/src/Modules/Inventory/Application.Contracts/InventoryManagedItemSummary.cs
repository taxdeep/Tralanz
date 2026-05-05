using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryManagedItemSummary(
    Guid Id,
    CompanyId CompanyId,
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
    string? DefaultInventoryAssetAccountLabel,
    Guid? DefaultCogsAccountId,
    string? DefaultCogsAccountLabel,
    Guid? DefaultWriteOffAccountId,
    string? DefaultWriteOffAccountLabel,
    Guid? DefaultPurchaseVarianceAccountId,
    string? DefaultPurchaseVarianceAccountLabel,
    bool IsActive,
    DateTimeOffset UpdatedAt);
