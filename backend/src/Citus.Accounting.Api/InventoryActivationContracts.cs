using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Accounting.Api;

/// <summary>
/// Wire shape for <c>POST /accounting/inventory/activate</c> — the
/// Inventory paid-module activation wizard's submit payload. Mirrors
/// the four wizard steps:
///   1. Profile tag (analytics + nav defaults).
///   2. Costing method (locked after first inventory transaction).
///   3. (Standard CoA accounts — no payload field; seeder is idempotent
///      and runs from the canonical template regardless.)
///   4. Default warehouse name (auto "Main Warehouse" if blank).
/// Step 5 (per-item opening balance) is deferred to a later milestone
/// once an OpeningBalanceReceipt helper exists; the wizard surfaces a
/// link to the existing Inventory Adjustment workbench instead.
/// </summary>
public sealed record InventoryActivationHttpRequest(
    string? ProfileTag,
    string? CostingMethod,
    string? WarehouseName);

public sealed record InventoryActivationHttpResponse(
    bool ModuleEnabled,
    DateTimeOffset? EnabledAt,
    DateTimeOffset? LockedAt,
    string? ProfileTag,
    string DefaultCostingMethod,
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    int CoaAccountsCreated,
    int CoaAccountsAlreadyPresent);

internal static class InventoryActivationRequestParser
{
    /// <summary>
    /// Maps wire <c>"moving_average"</c> / <c>"fifo"</c> to the domain
    /// enum. Defaults to MovingAverage on any unrecognised / null value
    /// — matches the wizard's recommended default.
    /// </summary>
    public static InventoryCostingMethod ParseCostingMethod(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "fifo" => InventoryCostingMethod.Fifo,
            _ => InventoryCostingMethod.MovingAverage
        };

    public static string FormatCostingMethod(InventoryCostingMethod method) => method switch
    {
        InventoryCostingMethod.Fifo => "fifo",
        _ => "moving_average"
    };

    public static string ResolveWarehouseName(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? "Main Warehouse"
            : raw.Trim();

    public static InventoryCostingPolicyUpdateRequest BuildPolicyUpdateRequest(
        CompanyId companyId,
        UserId userId,
        InventoryActivationHttpRequest request) =>
        new(
            CompanyId: companyId,
            UserId: userId,
            DefaultCostingMethod: ParseCostingMethod(request.CostingMethod),
            NegativeStockAllowed: false,
            RequireWriteOffApproval: true);

    public static InventoryWarehouseUpsertRequest BuildDefaultWarehouseRequest(
        CompanyId companyId,
        UserId userId,
        Guid? warehouseId,
        InventoryActivationHttpRequest request) =>
        new(
            CompanyId: companyId,
            UserId: userId,
            WarehouseId: warehouseId,
            WarehouseCode: "MAIN",
            Name: ResolveWarehouseName(request.WarehouseName),
            Description: "Default warehouse created by the Inventory activation wizard.");
}
