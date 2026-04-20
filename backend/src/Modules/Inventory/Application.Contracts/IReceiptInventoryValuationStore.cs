namespace Citus.Modules.Inventory.Application.Contracts;

public interface IReceiptInventoryValuationStore
{
    Task<ReceiptInventoryValuationSummary> RefreshReceiptValuationAsync(
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptInventoryValuationSummary?> GetReceiptValuationSummaryAsync(
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptInventoryValuationSummary>> GetReceiptValuationSummariesAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);
}
