namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryIssueStore
{
    Task<InventorySalesIssueDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InventorySalesIssueSummary> PostAsync(
        InventorySalesIssuePostRequest request,
        CancellationToken cancellationToken);
}
