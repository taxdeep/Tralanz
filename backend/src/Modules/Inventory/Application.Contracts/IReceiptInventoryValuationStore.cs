namespace Citus.Modules.Inventory.Application.Contracts;

public interface IReceiptInventoryValuationStore
{
    Task<ReceiptInventoryValuationSummary> RefreshReceiptValuationAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptInventoryValuationSummary?> GetReceiptValuationSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptInventoryValuationSummary>> GetReceiptValuationSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);
}
