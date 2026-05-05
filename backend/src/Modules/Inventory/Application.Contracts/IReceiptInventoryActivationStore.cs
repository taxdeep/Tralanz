namespace Citus.Modules.Inventory.Application.Contracts;

public interface IReceiptInventoryActivationStore
{
    Task ValidateCanActivateAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptInventoryActivationSummary> ActivatePostedReceiptAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task RecordActivationFailureAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        string failureMessage,
        CancellationToken cancellationToken);

    Task<ReceiptInventoryActivationSummary?> GetReceiptActivationSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptInventoryActivationSummary>> GetReceiptActivationSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);
}
