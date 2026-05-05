namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryReportingStore
{
    Task<InventoryAvailabilityDashboard> GetAvailabilityDashboardAsync(
        CompanyId companyId,
        InventoryAvailabilityFilter filter,
        CancellationToken cancellationToken);
}
