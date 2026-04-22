namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryTransferStore
{
    Task<InventoryTransferDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> UpsertAsync(
        InventoryTransferUpsertRequest request,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> SubmitAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> ShipAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        DateOnly postingDate,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> ReceiveAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        DateOnly postingDate,
        CancellationToken cancellationToken);
}
