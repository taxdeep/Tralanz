using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Modules.Inventory.Application;

public sealed class InventoryReportingWorkflow
{
    private readonly IInventoryReportingStore _store;

    public InventoryReportingWorkflow(IInventoryReportingStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InventoryAvailabilityDashboard> GetAvailabilityDashboardAsync(
        CompanyId companyId,
        InventoryAvailabilityFilter? filter,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        return _store.GetAvailabilityDashboardAsync(
            companyId,
            filter ?? new InventoryAvailabilityFilter(null, null),
            cancellationToken);
    }
}
