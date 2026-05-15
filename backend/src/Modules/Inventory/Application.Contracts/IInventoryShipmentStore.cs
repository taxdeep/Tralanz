namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryShipmentStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<InventoryShipmentDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<InventoryShipmentSummary?> GetAsync(
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken);

    Task<InventoryInvoiceShipmentHandoffSummary> GetInvoiceHandoffSummaryAsync(
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken);

    Task<InventoryInvoiceShipmentIssueLaneSummary> GetInvoiceLaneSummaryAsync(
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, InventoryInvoiceShipmentPostingGateSnapshot>> GetInvoicePostingGateSnapshotsAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> invoiceDocumentIds,
        CancellationToken cancellationToken);

    Task<InventoryShipmentSummary> PostAsync(
        InventoryShipmentPostRequest request,
        CancellationToken cancellationToken);
}
