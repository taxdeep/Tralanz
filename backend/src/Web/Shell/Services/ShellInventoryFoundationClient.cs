using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;

namespace Web.Shell.Services;

public sealed class ShellInventoryFoundationClient(InventoryFoundationWorkflow workflow)
{
    public async Task<InventoryFoundationDashboard> EnsureDashboardAsync(
        Guid companyId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await workflow.EnsureAsync(
            new InventoryFoundationEnsureRequest(
                companyId,
                userId,
                InventoryCostingMethod.MovingAverage,
                NegativeStockAllowed: false,
                RequireWriteOffApproval: true),
            cancellationToken);

        return await workflow.GetDashboardAsync(companyId, cancellationToken);
    }

    public Task<InventoryFoundationDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        workflow.GetDashboardAsync(companyId, cancellationToken);

    public async Task<InventoryCostingPolicyRecord> SavePolicyAsync(
        InventoryCostingPolicyUpdateRequest request,
        InventoryFoundationDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryFoundationRules.ValidatePolicySave(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.SavePolicyAsync(request, cancellationToken);
    }

    public async Task<Guid> SaveItemAsync(
        InventoryItemUpsertRequest request,
        InventoryFoundationDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryFoundationRules.ValidateItemSave(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.SaveItemAsync(request, cancellationToken);
    }

    public async Task SetItemActiveAsync(
        Guid companyId,
        Guid itemId,
        bool isActive,
        InventoryFoundationDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var item = dashboard?.Items.FirstOrDefault(current => current.Id == itemId);
        var validation = ShellInventoryFoundationRules.ValidateItemActiveStateChange(item, isActive, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        await workflow.SetItemActiveAsync(companyId, itemId, isActive, cancellationToken);
    }

    public async Task<Guid> SaveWarehouseAsync(
        InventoryWarehouseUpsertRequest request,
        InventoryFoundationDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryFoundationRules.ValidateWarehouseSave(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.SaveWarehouseAsync(request, cancellationToken);
    }

    public async Task SetWarehouseActiveAsync(
        Guid companyId,
        Guid warehouseId,
        bool isActive,
        InventoryFoundationDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var warehouse = dashboard?.Warehouses.FirstOrDefault(current => current.Id == warehouseId);
        var validation = ShellInventoryFoundationRules.ValidateWarehouseActiveStateChange(warehouse, isActive, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        await workflow.SetWarehouseActiveAsync(companyId, warehouseId, isActive, cancellationToken);
    }
}
