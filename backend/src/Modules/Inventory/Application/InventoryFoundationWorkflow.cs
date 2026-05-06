using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application;

public sealed class InventoryFoundationWorkflow
{
    private readonly IInventoryFoundationStore _store;

    public InventoryFoundationWorkflow(IInventoryFoundationStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InventoryFoundationSummary> GetSummaryAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        return _store.GetSummaryAsync(companyId, cancellationToken);
    }

    public Task<InventoryFoundationDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        return _store.GetDashboardAsync(companyId, cancellationToken);
    }

    public Task<InventoryFoundationSummary> EnsureAsync(
        InventoryFoundationEnsureRequest request,
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

        return _store.EnsureCompanyFoundationAsync(request, cancellationToken);
    }

    public Task<InventoryCostingPolicyRecord> SavePolicyAsync(
        InventoryCostingPolicyUpdateRequest request,
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

        return _store.SavePolicyAsync(request, cancellationToken);
    }

    public Task<Guid> SaveItemAsync(
        InventoryItemUpsertRequest request,
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

        return _store.SaveItemAsync(request, cancellationToken);
    }

    public Task SetItemActiveAsync(
        CompanyId companyId,
        Guid itemId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item id is required.", nameof(itemId));
        }

        return _store.SetItemActiveAsync(companyId, itemId, isActive, cancellationToken);
    }

    public Task<Guid> SaveWarehouseAsync(
        InventoryWarehouseUpsertRequest request,
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

        return _store.SaveWarehouseAsync(request, cancellationToken);
    }

    public Task SetWarehouseActiveAsync(
        CompanyId companyId,
        Guid warehouseId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (warehouseId == Guid.Empty)
        {
            throw new ArgumentException("Warehouse id is required.", nameof(warehouseId));
        }

        return _store.SetWarehouseActiveAsync(companyId, warehouseId, isActive, cancellationToken);
    }
}
