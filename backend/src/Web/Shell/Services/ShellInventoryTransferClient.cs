using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public sealed class ShellInventoryTransferClient(InventoryTransferWorkflow workflow)
{
    public Task<InventoryTransferDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        workflow.GetDashboardAsync(companyId, cancellationToken);

    public async Task<InventoryTransferSummary> SaveDraftAsync(
        InventoryTransferUpsertRequest request,
        InventoryTransferDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryTransferRules.ValidateUpsert(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.UpsertAsync(request, cancellationToken);
    }

    public Task<InventoryTransferSummary> SubmitAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        InventoryTransferDashboard? dashboard,
        CancellationToken cancellationToken = default) =>
        SubmitCoreAsync(companyId, transferId, userId, dashboard, cancellationToken);

    public Task<InventoryTransferSummary> ShipAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        DateOnly postingDate,
        InventoryTransferDashboard? dashboard,
        CancellationToken cancellationToken = default) =>
        ShipCoreAsync(companyId, transferId, userId, postingDate, dashboard, cancellationToken);

    public Task<InventoryTransferSummary> ReceiveAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        DateOnly postingDate,
        InventoryTransferDashboard? dashboard,
        CancellationToken cancellationToken = default) =>
        ReceiveCoreAsync(companyId, transferId, userId, postingDate, dashboard, cancellationToken);

    private async Task<InventoryTransferSummary> SubmitCoreAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        InventoryTransferDashboard? dashboard,
        CancellationToken cancellationToken)
    {
        var validation = ShellInventoryTransferRules.ValidateSubmit(companyId, transferId, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.SubmitAsync(companyId, transferId, userId, cancellationToken);
    }

    private async Task<InventoryTransferSummary> ShipCoreAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        DateOnly postingDate,
        InventoryTransferDashboard? dashboard,
        CancellationToken cancellationToken)
    {
        var validation = ShellInventoryTransferRules.ValidateShip(companyId, transferId, postingDate, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.ShipAsync(companyId, transferId, userId, postingDate, cancellationToken);
    }

    private async Task<InventoryTransferSummary> ReceiveCoreAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        DateOnly postingDate,
        InventoryTransferDashboard? dashboard,
        CancellationToken cancellationToken)
    {
        var validation = ShellInventoryTransferRules.ValidateReceive(companyId, transferId, postingDate, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.ReceiveAsync(companyId, transferId, userId, postingDate, cancellationToken);
    }
}
