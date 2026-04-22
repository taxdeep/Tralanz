namespace Citus.Modules.Inventory.Domain.Shared;

public enum InventoryDocumentStatus
{
    Draft = 0,
    Submitted = 1,
    Posted = 2,
    Cancelled = 3,
    Shipped = 4,
    Received = 5
}
