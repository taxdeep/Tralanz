namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryShipmentStore
{
    Task<InventoryShipmentDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InventoryShipmentSummary?> GetAsync(
        Guid companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken);

    Task<InventoryInvoiceShipmentHandoffSummary> GetInvoiceHandoffSummaryAsync(
        Guid companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken);

    Task<InventoryInvoiceShipmentIssueLaneSummary> GetInvoiceLaneSummaryAsync(
        Guid companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, InventoryInvoiceShipmentPostingGateSnapshot>> GetInvoicePostingGateSnapshotsAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> invoiceDocumentIds,
        CancellationToken cancellationToken);

    Task<InventoryShipmentSummary> PostAsync(
        InventoryShipmentPostRequest request,
        CancellationToken cancellationToken);
}
