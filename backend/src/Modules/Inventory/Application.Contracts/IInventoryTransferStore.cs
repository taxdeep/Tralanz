namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryTransferStore
{
    Task<InventoryTransferDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> UpsertAsync(
        InventoryTransferUpsertRequest request,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> SubmitAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> ShipAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        DateOnly postingDate,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> ReceiveAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        DateOnly postingDate,
        CancellationToken cancellationToken);
}
