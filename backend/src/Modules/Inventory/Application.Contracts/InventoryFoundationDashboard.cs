namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryFoundationDashboard(
    InventoryFoundationSummary Summary,
    IReadOnlyList<InventoryManagedItemSummary> Items,
    IReadOnlyList<InventoryManagedWarehouseSummary> Warehouses,
    IReadOnlyList<InventoryFoundationAccountOption> InventoryAssetAccountOptions,
    IReadOnlyList<InventoryFoundationAccountOption> ExpenseAccountOptions);

public sealed record class InventoryFoundationAccountOption(
    Guid AccountId,
    string Code,
    string Name,
    string RootType,
    string DetailType,
    string CurrencyCode)
{
    public string DisplayText => $"{Code} {Name}";
}
