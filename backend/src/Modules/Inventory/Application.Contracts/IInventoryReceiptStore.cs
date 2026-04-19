namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryReceiptStore
{
    Task<InventoryPurchaseReceiptDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InventoryBillReceiptHandoffSummary> GetBillHandoffSummaryAsync(
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken);

    Task<InventoryPurchaseReceiptSummary> PostAsync(
        InventoryPurchaseReceiptPostRequest request,
        CancellationToken cancellationToken);
}
