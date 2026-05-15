namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryReturnStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<InventoryReturnReceiveDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<InventoryReturnReceiveHandoffSummary> GetShipmentHandoffSummaryAsync(
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken);

    Task<InventoryReturnReceiveSummary> PostAsync(
        InventoryReturnReceivePostRequest request,
        CancellationToken cancellationToken);
}
