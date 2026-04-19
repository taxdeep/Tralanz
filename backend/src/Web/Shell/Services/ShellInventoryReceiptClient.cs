using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public sealed class ShellInventoryReceiptClient(InventoryReceiptWorkflow workflow)
{
    public Task<InventoryPurchaseReceiptDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        workflow.GetDashboardAsync(companyId, cancellationToken);

    public Task<InventoryBillReceiptHandoffSummary> GetBillHandoffSummaryAsync(
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken = default) =>
        workflow.GetBillHandoffSummaryAsync(companyId, billDocumentId, cancellationToken);

    public async Task<InventoryPurchaseReceiptSummary> PostAsync(
        InventoryPurchaseReceiptPostRequest request,
        InventoryPurchaseReceiptDashboard? dashboard,
        ShellCounterpartyOnboardingSummary? counterparties,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryReceiptRules.ValidatePost(request, dashboard, counterparties);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.PostAsync(request, cancellationToken);
    }
}
