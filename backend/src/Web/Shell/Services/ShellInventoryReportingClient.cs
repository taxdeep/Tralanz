using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public sealed class ShellInventoryReportingClient(InventoryReportingWorkflow workflow)
{
    public Task<InventoryAvailabilityDashboard> GetAvailabilityDashboardAsync(
        Guid companyId,
        InventoryAvailabilityFilter? filter = null,
        CancellationToken cancellationToken = default) =>
        workflow.GetAvailabilityDashboardAsync(companyId, filter, cancellationToken);
}
