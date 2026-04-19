using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public sealed class ShellInventoryAdjustmentClient(InventoryAdjustmentWorkflow workflow)
{
    public Task<InventoryAdjustmentDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        workflow.GetDashboardAsync(companyId, cancellationToken);

    public async Task<InventoryAdjustmentSummary> RequestWriteOffAsync(
        InventoryWriteOffRequestPostRequest request,
        InventoryAdjustmentDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryAdjustmentRules.ValidateWriteOffRequest(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.RequestWriteOffAsync(request, cancellationToken);
    }

    public async Task<InventoryAdjustmentSummary> ApproveWriteOffAsync(
        InventoryWriteOffApprovePostRequest request,
        InventoryAdjustmentDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryAdjustmentRules.ValidateWriteOffApproval(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.ApproveWriteOffAsync(request, cancellationToken);
    }

    public async Task<InventoryAdjustmentSummary> PostApprovedWriteOffAsync(
        InventoryWriteOffApprovePostRequest request,
        InventoryAdjustmentDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryAdjustmentRules.ValidateWriteOffPost(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.PostApprovedWriteOffAsync(request, cancellationToken);
    }

    public async Task<InventoryAdjustmentSummary> PostAsync(
        InventoryAdjustmentPostRequest request,
        InventoryAdjustmentDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryAdjustmentRules.ValidatePost(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.PostAsync(request, cancellationToken);
    }
}
