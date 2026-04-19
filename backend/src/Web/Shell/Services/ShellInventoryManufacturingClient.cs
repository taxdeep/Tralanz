using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public sealed class ShellInventoryManufacturingClient(InventoryManufacturingWorkflow workflow)
{
    public Task<InventoryManufacturingDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        workflow.GetDashboardAsync(companyId, cancellationToken);

    public async Task<InventoryBomSummary> SaveBomAsync(
        InventoryBomUpsertRequest request,
        InventoryManufacturingDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryManufacturingRules.ValidateBom(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.UpsertBomAsync(request, cancellationToken);
    }

    public async Task<InventoryManufacturingSummary> PostAsync(
        InventoryManufacturingPostRequest request,
        InventoryManufacturingDashboard? dashboard,
        CancellationToken cancellationToken = default)
    {
        var validation = ShellInventoryManufacturingRules.ValidatePost(request, dashboard);
        if (!validation.Succeeded)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        return await workflow.PostAsync(request, cancellationToken);
    }
}
