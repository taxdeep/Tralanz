namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryIssueStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<InventorySalesIssueDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<InventoryInvoiceIssueHandoffSummary> GetInvoiceHandoffSummaryAsync(
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, InventoryInvoiceIssuePostingGateSnapshot>> GetInvoicePostingGateSnapshotsAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> invoiceDocumentIds,
        CancellationToken cancellationToken);

    Task<InventorySalesIssueSummary> PostAsync(
        InventorySalesIssuePostRequest request,
        CancellationToken cancellationToken);
}
