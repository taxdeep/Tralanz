using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Modules.Inventory.Application;

public sealed class InventoryShipmentWorkflow
{
    private readonly IInventoryShipmentStore _store;

    public InventoryShipmentWorkflow(IInventoryShipmentStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InventoryShipmentDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        return _store.GetDashboardAsync(companyId, cancellationToken);
    }

    public Task<InventoryShipmentSummary?> GetAsync(
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (shipmentDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Shipment document id is required.", nameof(shipmentDocumentId));
        }

        return _store.GetAsync(companyId, shipmentDocumentId, cancellationToken);
    }

    public Task<InventoryInvoiceShipmentHandoffSummary> GetInvoiceHandoffSummaryAsync(
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (invoiceDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Invoice document id is required.", nameof(invoiceDocumentId));
        }

        return _store.GetInvoiceHandoffSummaryAsync(companyId, invoiceDocumentId, cancellationToken);
    }

    public Task<InventoryInvoiceShipmentIssueLaneSummary> GetInvoiceLaneSummaryAsync(
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (invoiceDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Invoice document id is required.", nameof(invoiceDocumentId));
        }

        return _store.GetInvoiceLaneSummaryAsync(companyId, invoiceDocumentId, cancellationToken);
    }

    public Task<InventoryShipmentSummary> PostAsync(
        InventoryShipmentPostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CompanyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(request));
        }

        if (request.UserId.Value is null)
        {
            throw new ArgumentException("User id is required.", nameof(request));
        }

        if (request.CustomerId == Guid.Empty)
        {
            throw new InvalidOperationException("Customer id is required.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new InvalidOperationException("At least one shipment line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0)
            {
                throw new InvalidOperationException("Shipment line numbers must be positive.");
            }

            if (!seenLineNumbers.Add(line.LineNo))
            {
                throw new InvalidOperationException("Shipment line numbers must be unique.");
            }

            if (line.ItemId == Guid.Empty)
            {
                throw new InvalidOperationException("Each shipment line must select an inventory item.");
            }

            if (line.WarehouseId == Guid.Empty)
            {
                throw new InvalidOperationException("Each shipment line must select a warehouse.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                throw new InvalidOperationException("Each shipment line must include a UOM code.");
            }

            if (line.Quantity <= 0m)
            {
                throw new InvalidOperationException("Shipment quantities must be positive.");
            }
        }

        return _store.PostAsync(request, cancellationToken);
    }
}
