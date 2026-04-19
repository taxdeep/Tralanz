using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryFoundationStore
{
    Task<InventoryFoundationSummary> GetSummaryAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InventoryFoundationDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InventoryFoundationSummary> EnsureCompanyFoundationAsync(
        InventoryFoundationEnsureRequest request,
        CancellationToken cancellationToken);

    Task<InventoryCostingPolicyRecord> SavePolicyAsync(
        InventoryCostingPolicyUpdateRequest request,
        CancellationToken cancellationToken);

    Task<Guid> SaveItemAsync(
        InventoryItemUpsertRequest request,
        CancellationToken cancellationToken);

    Task SetItemActiveAsync(
        Guid companyId,
        Guid itemId,
        bool isActive,
        CancellationToken cancellationToken);

    Task<Guid> SaveWarehouseAsync(
        InventoryWarehouseUpsertRequest request,
        CancellationToken cancellationToken);

    Task SetWarehouseActiveAsync(
        Guid companyId,
        Guid warehouseId,
        bool isActive,
        CancellationToken cancellationToken);
}
