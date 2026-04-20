namespace Citus.Modules.Inventory.Application.Contracts;

public interface IReceiptInventoryCostLayerEmissionStore
{
    Task<ReceiptInventoryCostLayerEmissionSummary> EmitReceiptCostLayersAsync(
        Guid companyId,
        Guid userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<ReceiptInventoryCostLayerEmissionSummary?> GetReceiptCostLayerEmissionSummaryAsync(
        Guid companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionSummary>> GetReceiptCostLayerEmissionSummariesAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken);
}
