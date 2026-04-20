namespace Citus.Modules.Inventory.Application.Contracts;

public interface IReceiptGrIrBridgeStore
{
    Task<ReceiptGrIrBridgeSummary> RefreshReceiptGrIrBridgeAsync(
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptGrIrBridgeSummary?> GetReceiptGrIrBridgeSummaryAsync(
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptGrIrBridgeSummary>> GetReceiptGrIrBridgeSummariesAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);
}
