namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryAdjustmentStore
{
    Task<InventoryAdjustmentDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<InventoryAdjustmentSummary> RequestWriteOffAsync(
        InventoryWriteOffRequestPostRequest request,
        CancellationToken cancellationToken);

    Task<InventoryAdjustmentSummary> ApproveWriteOffAsync(
        InventoryWriteOffApprovePostRequest request,
        CancellationToken cancellationToken);

    Task<InventoryAdjustmentSummary> PostApprovedWriteOffAsync(
        InventoryWriteOffApprovePostRequest request,
        CancellationToken cancellationToken);

    Task<InventoryAdjustmentSummary> PostAsync(
        InventoryAdjustmentPostRequest request,
        CancellationToken cancellationToken);
}
