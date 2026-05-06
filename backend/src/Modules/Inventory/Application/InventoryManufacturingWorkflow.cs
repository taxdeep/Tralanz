using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Modules.Inventory.Application;

public sealed class InventoryManufacturingWorkflow
{
    private readonly IInventoryManufacturingStore _store;

    public InventoryManufacturingWorkflow(IInventoryManufacturingStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InventoryManufacturingDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        _store.GetDashboardAsync(companyId, cancellationToken);

    public async Task<InventoryBomSummary> UpsertBomAsync(
        InventoryBomUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CompanyId.Value is null)
        {
            throw new InvalidOperationException("Manufacturing BOM must stay inside a company.");
        }

        if (request.UserId.Value is null)
        {
            throw new InvalidOperationException("Manufacturing BOM save requires the active user.");
        }

        if (string.IsNullOrWhiteSpace(request.BomCode))
        {
            throw new InvalidOperationException("BOM code is required.");
        }

        if (request.OutputItemId == Guid.Empty)
        {
            throw new InvalidOperationException("BOM output item is required.");
        }

        if (request.OutputQuantity <= 0)
        {
            throw new InvalidOperationException("BOM output quantity must be greater than zero.");
        }

        if (request.Components is null || request.Components.Count == 0)
        {
            throw new InvalidOperationException("At least one BOM component line is required.");
        }

        return await _store.UpsertBomAsync(request, cancellationToken);
    }

    public async Task<InventoryManufacturingSummary> PostAsync(
        InventoryManufacturingPostRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CompanyId.Value is null)
        {
            throw new InvalidOperationException("Manufacturing post must stay inside a company.");
        }

        if (request.UserId.Value is null)
        {
            throw new InvalidOperationException("Manufacturing post requires the active user.");
        }

        if (request.BomId == Guid.Empty)
        {
            throw new InvalidOperationException("Choose a BOM before posting manufacturing.");
        }

        if (request.WarehouseId == Guid.Empty)
        {
            throw new InvalidOperationException("Choose a warehouse before posting manufacturing.");
        }

        if (request.OutputQuantity <= 0)
        {
            throw new InvalidOperationException("Manufacturing output quantity must be greater than zero.");
        }

        return await _store.PostAsync(request, cancellationToken);
    }
}
