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

    Task<IReadOnlyDictionary<Guid, InventoryBillReceiptPostingGateSnapshot>> GetBillPostingGateSnapshotsAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> billDocumentIds,
        CancellationToken cancellationToken);

    Task<InventoryPurchaseReceiptSummary> PostAsync(
        InventoryPurchaseReceiptPostRequest request,
        CancellationToken cancellationToken);
}
