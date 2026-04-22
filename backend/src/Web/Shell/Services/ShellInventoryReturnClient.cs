using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public sealed class ShellInventoryReturnClient(InventoryReturnWorkflow workflow)
{
    public Task<InventoryReturnReceiveDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        workflow.GetDashboardAsync(companyId, cancellationToken);

    public Task<InventoryReturnReceiveHandoffSummary> GetShipmentHandoffSummaryAsync(
        Guid companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken = default) =>
        workflow.GetShipmentHandoffSummaryAsync(companyId, shipmentDocumentId, cancellationToken);

    public async Task<InventoryReturnReceiveSummary> PostAsync(
        InventoryReturnReceivePostRequest request,
        InventoryReturnReceiveHandoffSummary? handoffSummary,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryReturnRules.ValidatePost(request, handoffSummary);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.PostAsync(request, cancellationToken);
    }
}
