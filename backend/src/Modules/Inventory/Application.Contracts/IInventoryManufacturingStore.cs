namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryManufacturingStore
{
    Task<InventoryManufacturingDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InventoryBomSummary> UpsertBomAsync(
        InventoryBomUpsertRequest request,
        CancellationToken cancellationToken);

    Task<InventoryManufacturingSummary> PostAsync(
        InventoryManufacturingPostRequest request,
        CancellationToken cancellationToken);
}
