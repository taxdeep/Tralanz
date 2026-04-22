using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public sealed class ShellInventoryIssueClient(InventoryIssueWorkflow workflow)
{
    public Task<InventorySalesIssueDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        workflow.GetDashboardAsync(companyId, cancellationToken);

    public Task<InventoryInvoiceIssueHandoffSummary> GetInvoiceHandoffSummaryAsync(
        Guid companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken = default) =>
        workflow.GetInvoiceHandoffSummaryAsync(companyId, invoiceDocumentId, cancellationToken);

    public async Task<InventorySalesIssueSummary> PostAsync(
        InventorySalesIssuePostRequest request,
        InventorySalesIssueDashboard? dashboard,
        ShellCounterpartyOnboardingSummary? counterparties,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryIssueRules.ValidatePost(request, dashboard, counterparties);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.PostAsync(request, cancellationToken);
    }
}
