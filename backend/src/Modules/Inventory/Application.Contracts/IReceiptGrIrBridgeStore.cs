namespace Citus.Modules.Inventory.Application.Contracts;

public interface IReceiptGrIrBridgeStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<ReceiptGrIrBridgeSummary> RefreshReceiptGrIrBridgeAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptGrIrBridgeSummary?> GetReceiptGrIrBridgeSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptGrIrBridgeSummary>> GetReceiptGrIrBridgeSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);
}
