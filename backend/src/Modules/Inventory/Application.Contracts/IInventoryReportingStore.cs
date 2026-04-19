namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryReportingStore
{
    Task<InventoryAvailabilityDashboard> GetAvailabilityDashboardAsync(
        Guid companyId,
        InventoryAvailabilityFilter filter,
        CancellationToken cancellationToken);
}
