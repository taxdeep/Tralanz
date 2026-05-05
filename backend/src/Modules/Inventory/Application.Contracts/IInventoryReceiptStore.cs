namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryReceiptStore
{
    Task<InventoryPurchaseReceiptDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<InventoryBillReceiptHandoffSummary> GetBillHandoffSummaryAsync(
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, InventoryBillReceiptPostingGateSnapshot>> GetBillPostingGateSnapshotsAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> billDocumentIds,
        CancellationToken cancellationToken);

    Task<LegacyInboundReceiptPathSnapshot?> GetLegacyInboundReceiptPathSnapshotAsync(
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken);

    Task<InventoryPurchaseReceiptSummary> PostAsync(
        InventoryPurchaseReceiptPostRequest request,
        CancellationToken cancellationToken);
}
