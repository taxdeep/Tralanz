namespace Citus.Modules.Inventory.Application.Contracts;

public interface IReceiptInventoryCostLayerEmissionStore
{
    Task<ReceiptInventoryCostLayerEmissionSummary> EmitReceiptCostLayersAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptInventoryCostLayerEmissionSummary?> GetReceiptCostLayerEmissionSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionSummary>> GetReceiptCostLayerEmissionSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);

    Task<ReceiptInventoryCostLayerEmissionReconciliationSummary?> GetReceiptCostLayerEmissionReconciliationSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionReconciliationSummary>> GetReceiptCostLayerEmissionReconciliationSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);
}
