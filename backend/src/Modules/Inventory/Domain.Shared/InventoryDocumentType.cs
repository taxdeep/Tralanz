namespace Citus.Modules.Inventory.Domain.Shared;

public enum InventoryDocumentType
{
    PurchaseReceipt = 0,
    CustomerReturnReceipt = 1,
    TransferReceive = 2,
    ManufacturingReceipt = 3,
    OpeningBalanceReceipt = 4,
    InventoryAdjustmentGain = 5,
    SalesIssue = 6,
    VendorReturnIssue = 7,
    TransferShip = 8,
    ManufacturingIssue = 9,
    InventoryWriteOff = 10,
    InventoryAdjustmentLoss = 11
}
