namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryReturnStore
{
    Task<InventoryReturnReceiveDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InventoryReturnReceiveHandoffSummary> GetShipmentHandoffSummaryAsync(
        Guid companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken);

    Task<InventoryReturnReceiveSummary> PostAsync(
        InventoryReturnReceivePostRequest request,
        CancellationToken cancellationToken);
}
