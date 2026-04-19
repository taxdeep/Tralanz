using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Modules.Inventory.Application;

public sealed class InventoryIssueWorkflow
{
    private readonly IInventoryIssueStore _store;

    public InventoryIssueWorkflow(IInventoryIssueStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InventorySalesIssueDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        return _store.GetDashboardAsync(companyId, cancellationToken);
    }

    public Task<InventorySalesIssueSummary> PostAsync(
        InventorySalesIssuePostRequest request,
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
            throw new ArgumentException("Customer id is required.", nameof(request));
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new InvalidOperationException("At least one sales issue line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0)
            {
                throw new InvalidOperationException("Sales issue line numbers must be positive.");
            }

            if (!seenLineNumbers.Add(line.LineNo))
            {
                throw new InvalidOperationException("Sales issue line numbers must be unique.");
            }

            if (line.ItemId == Guid.Empty)
            {
                throw new InvalidOperationException("Each sales issue line must select an inventory item.");
            }

            if (line.WarehouseId == Guid.Empty)
            {
                throw new InvalidOperationException("Each sales issue line must select a warehouse.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                throw new InvalidOperationException("Each sales issue line must include a UOM code.");
            }

            if (line.Quantity <= 0)
            {
                throw new InvalidOperationException("Sales issue quantities must be positive.");
            }
        }

        return _store.PostAsync(request, cancellationToken);
    }
}
