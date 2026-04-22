namespace Citus.Modules.Inventory.Domain.Shared;

public enum InventoryLowStockActivity
{
    Nothing = 0,
    Warn = 1,
    BlockOutbound = 2
}
