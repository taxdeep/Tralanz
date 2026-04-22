using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Modules.Inventory.Application;

public sealed class InventoryReturnWorkflow
{
    private readonly IInventoryReturnStore _store;

    public InventoryReturnWorkflow(IInventoryReturnStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InventoryReturnReceiveDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        return _store.GetDashboardAsync(companyId, cancellationToken);
    }

    public Task<InventoryReturnReceiveHandoffSummary> GetShipmentHandoffSummaryAsync(
        Guid companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (shipmentDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Shipment document id is required.", nameof(shipmentDocumentId));
        }

        return _store.GetShipmentHandoffSummaryAsync(companyId, shipmentDocumentId, cancellationToken);
    }

    public Task<InventoryReturnReceiveSummary> PostAsync(
        InventoryReturnReceivePostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CompanyId == Guid.Empty)
        {
            throw new ArgumentException("Company id is required.", nameof(request));
        }

        if (request.UserId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(request));
        }

        if (request.CustomerId == Guid.Empty)
        {
            throw new InvalidOperationException("Customer id is required.");
        }

        if (request.ShipmentDocumentId == Guid.Empty)
        {
            throw new InvalidOperationException("Shipment anchor is required.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new InvalidOperationException("At least one return line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0)
            {
                throw new InvalidOperationException("Return line numbers must be positive.");
            }

            if (!seenLineNumbers.Add(line.LineNo))
            {
                throw new InvalidOperationException("Return line numbers must be unique.");
            }

            if (line.ItemId == Guid.Empty)
            {
                throw new InvalidOperationException("Each return line must select an inventory item.");
            }

            if (line.WarehouseId == Guid.Empty)
            {
                throw new InvalidOperationException("Each return line must select a warehouse.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                throw new InvalidOperationException("Each return line must include a UOM code.");
            }

            if (line.Quantity <= 0m)
            {
                throw new InvalidOperationException("Return quantities must be positive.");
            }

            if (string.IsNullOrWhiteSpace(line.ConditionCode))
            {
                throw new InvalidOperationException("Each return line must include a condition code.");
            }

            if (string.IsNullOrWhiteSpace(line.ReturnReasonCode))
            {
                throw new InvalidOperationException("Each return line must include a return reason code.");
            }
        }

        return _store.PostAsync(request, cancellationToken);
    }
}
