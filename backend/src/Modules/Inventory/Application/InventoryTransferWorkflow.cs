using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Modules.Inventory.Application;

public sealed class InventoryTransferWorkflow
{
    private readonly IInventoryTransferStore _store;

    public InventoryTransferWorkflow(IInventoryTransferStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InventoryTransferDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        return _store.GetDashboardAsync(companyId, cancellationToken);
    }

    public Task<InventoryTransferSummary> UpsertAsync(
        InventoryTransferUpsertRequest request,
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

        if (request.SourceWarehouseId == Guid.Empty || request.DestinationWarehouseId == Guid.Empty)
        {
            throw new InvalidOperationException("Both source and destination warehouses are required.");
        }

        if (request.SourceWarehouseId == request.DestinationWarehouseId)
        {
            throw new InvalidOperationException("Source and destination warehouses must be different.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new InvalidOperationException("At least one transfer line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0)
            {
                throw new InvalidOperationException("Transfer line numbers must be positive.");
            }

            if (!seenLineNumbers.Add(line.LineNo))
            {
                throw new InvalidOperationException("Transfer line numbers must be unique.");
            }

            if (line.ItemId == Guid.Empty)
            {
                throw new InvalidOperationException("Each transfer line must select an inventory item.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                throw new InvalidOperationException("Each transfer line must include a UOM code.");
            }

            if (line.Quantity <= 0)
            {
                throw new InvalidOperationException("Transfer quantities must be positive.");
            }
        }

        return _store.UpsertAsync(request, cancellationToken);
    }

    public Task<InventoryTransferSummary> SubmitAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (transferId == Guid.Empty)
        {
            throw new ArgumentException("Transfer id is required.", nameof(transferId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        return _store.SubmitAsync(companyId, transferId, userId, cancellationToken);
    }

    public Task<InventoryTransferSummary> ShipAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        DateOnly postingDate,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (transferId == Guid.Empty)
        {
            throw new ArgumentException("Transfer id is required.", nameof(transferId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        return _store.ShipAsync(companyId, transferId, userId, postingDate, cancellationToken);
    }

    public Task<InventoryTransferSummary> ReceiveAsync(
        Guid companyId,
        Guid transferId,
        Guid userId,
        DateOnly postingDate,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (transferId == Guid.Empty)
        {
            throw new ArgumentException("Transfer id is required.", nameof(transferId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        return _store.ReceiveAsync(companyId, transferId, userId, postingDate, cancellationToken);
    }
}
