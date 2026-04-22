namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryIssueStore
{
    Task<InventorySalesIssueDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InventoryInvoiceIssueHandoffSummary> GetInvoiceHandoffSummaryAsync(
        Guid companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, InventoryInvoiceIssuePostingGateSnapshot>> GetInvoicePostingGateSnapshotsAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> invoiceDocumentIds,
        CancellationToken cancellationToken);

    Task<InventorySalesIssueSummary> PostAsync(
        InventorySalesIssuePostRequest request,
        CancellationToken cancellationToken);
}
