using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public sealed class ShellInventoryShipmentClient(InventoryShipmentWorkflow workflow)
{
    public Task<InventoryShipmentDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        workflow.GetDashboardAsync(companyId, cancellationToken);

    public Task<InventoryShipmentSummary?> GetAsync(
        Guid companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken = default) =>
        workflow.GetAsync(companyId, shipmentDocumentId, cancellationToken);

    public Task<InventoryInvoiceShipmentHandoffSummary> GetInvoiceHandoffSummaryAsync(
        Guid companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken = default) =>
        workflow.GetInvoiceHandoffSummaryAsync(companyId, invoiceDocumentId, cancellationToken);

    public Task<InventoryInvoiceShipmentIssueLaneSummary> GetInvoiceLaneSummaryAsync(
        Guid companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken = default) =>
        workflow.GetInvoiceLaneSummaryAsync(companyId, invoiceDocumentId, cancellationToken);

    public async Task<InventoryShipmentSummary> PostAsync(
        InventoryShipmentPostRequest request,
        InventoryShipmentDashboard? dashboard,
        ShellCounterpartyOnboardingSummary? counterparties,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryShipmentRules.ValidatePost(request, dashboard, counterparties);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.PostAsync(request, cancellationToken);
    }
}
