using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application;

public sealed class InventoryAdjustmentWorkflow
{
    private readonly IInventoryAdjustmentStore _store;

    public InventoryAdjustmentWorkflow(IInventoryAdjustmentStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InventoryAdjustmentDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        return _store.GetDashboardAsync(companyId, cancellationToken);
    }

    public Task<InventoryAdjustmentSummary> PostAsync(
        InventoryAdjustmentPostRequest request,
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

        if (request.WarehouseId == Guid.Empty)
        {
            throw new InvalidOperationException("Adjustment warehouse is required.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new InvalidOperationException("At least one adjustment line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0)
            {
                throw new InvalidOperationException("Adjustment line numbers must be positive.");
            }

            if (!seenLineNumbers.Add(line.LineNo))
            {
                throw new InvalidOperationException("Adjustment line numbers must be unique.");
            }

            if (line.ItemId == Guid.Empty)
            {
                throw new InvalidOperationException("Each adjustment line must select an inventory item.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                throw new InvalidOperationException("Each adjustment line must include a UOM code.");
            }

            if (line.Quantity <= 0)
            {
                throw new InvalidOperationException("Adjustment quantities must be positive.");
            }

            if (request.AdjustmentKind == InventoryAdjustmentKind.Gain &&
                (!line.UnitCostBase.HasValue || line.UnitCostBase.Value < 0))
            {
                throw new InvalidOperationException("Adjustment gain lines must include a non-negative unit cost.");
            }
        }

        return _store.PostAsync(request, cancellationToken);
    }

    public Task<InventoryAdjustmentSummary> RequestWriteOffAsync(
        InventoryWriteOffRequestPostRequest request,
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

        if (request.WarehouseId == Guid.Empty)
        {
            throw new InvalidOperationException("Write-off warehouse is required.");
        }

        ValidateLines(request.Lines, requireGainUnitCost: false);
        return _store.RequestWriteOffAsync(request, cancellationToken);
    }

    public Task<InventoryAdjustmentSummary> ApproveWriteOffAsync(
        InventoryWriteOffApprovePostRequest request,
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

        if (request.DocumentId == Guid.Empty)
        {
            throw new InvalidOperationException("Write-off document id is required.");
        }

        return _store.ApproveWriteOffAsync(request, cancellationToken);
    }

    public Task<InventoryAdjustmentSummary> PostApprovedWriteOffAsync(
        InventoryWriteOffApprovePostRequest request,
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

        if (request.DocumentId == Guid.Empty)
        {
            throw new InvalidOperationException("Write-off document id is required.");
        }

        return _store.PostApprovedWriteOffAsync(request, cancellationToken);
    }

    private static void ValidateLines(
        IReadOnlyList<InventoryAdjustmentLineInput>? lines,
        bool requireGainUnitCost)
    {
        if (lines is null || lines.Count == 0)
        {
            throw new InvalidOperationException("At least one adjustment line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in lines)
        {
            if (line.LineNo <= 0)
            {
                throw new InvalidOperationException("Adjustment line numbers must be positive.");
            }

            if (!seenLineNumbers.Add(line.LineNo))
            {
                throw new InvalidOperationException("Adjustment line numbers must be unique.");
            }

            if (line.ItemId == Guid.Empty)
            {
                throw new InvalidOperationException("Each adjustment line must select an inventory item.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                throw new InvalidOperationException("Each adjustment line must include a UOM code.");
            }

            if (line.Quantity <= 0)
            {
                throw new InvalidOperationException("Adjustment quantities must be positive.");
            }

            if (requireGainUnitCost &&
                (!line.UnitCostBase.HasValue || line.UnitCostBase.Value < 0))
            {
                throw new InvalidOperationException("Adjustment gain lines must include a non-negative unit cost.");
            }
        }
    }
}
