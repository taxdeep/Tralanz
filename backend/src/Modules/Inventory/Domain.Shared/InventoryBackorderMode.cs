namespace Citus.Modules.Inventory.Domain.Shared;

public enum InventoryBackorderMode
{
    Disallow = 0,
    AllowNegative = 1,
    AllowNegativeWithWarning = 2
}
