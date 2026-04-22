namespace Citus.Modules.Inventory.Application.Contracts;

public interface IReceiptInventoryActivationStore
{
    Task ValidateCanActivateAsync(
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptInventoryActivationSummary> ActivatePostedReceiptAsync(
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task RecordActivationFailureAsync(
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        string failureMessage,
        CancellationToken cancellationToken);

    Task<ReceiptInventoryActivationSummary?> GetReceiptActivationSummaryAsync(
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptInventoryActivationSummary>> GetReceiptActivationSummariesAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);
}
