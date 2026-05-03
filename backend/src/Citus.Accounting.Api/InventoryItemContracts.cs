using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Accounting.Api;

/// <summary>
/// Wire shape for POST/PUT <c>/accounting/items</c>. Strings on enum
/// fields keep the JSON friendly (callers send "stock" / "service" /
/// "non_stock") and the API translates to the strongly-typed enums
/// the application contract expects.
/// </summary>
public sealed record InventoryItemUpsertHttpRequest(
    string ItemCode,
    string Name,
    string? Description,
    string ItemKind,
    string? StockUomCode,
    string? ManageInventoryMethod,
    string? DefaultCostingMethod,
    string? BackorderMode,
    string? LowStockActivity,
    Guid? DefaultInventoryAssetAccountId,
    Guid? DefaultCogsAccountId,
    Guid? DefaultWriteOffAccountId,
    Guid? DefaultPurchaseVarianceAccountId,
    Guid? DefaultSalesRevenueAccountId,
    decimal? DefaultSalesPrice,
    decimal? DefaultPurchasePrice,
    Guid? DefaultSalesTaxCodeId,
    Guid? DefaultPurchaseTaxCodeId);

internal static class InventoryItemRequestMapper
{
    /// <summary>
    /// Returns null when the request is well-formed, or a user-displayable
    /// validation message otherwise. Kept defensive — the Blazor form is
    /// the primary validator, but a missing / typo'd payload from a
    /// scripted client should still produce a clean 400 instead of a
    /// stack trace.
    /// </summary>
    public static string? ValidateItemRequest(InventoryItemUpsertHttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ItemCode)) return "Item code is required.";
        if (string.IsNullOrWhiteSpace(request.Name)) return "Item name is required.";
        if (string.IsNullOrWhiteSpace(request.ItemKind)) return "Item kind is required.";

        if (!TryParseItemKind(request.ItemKind, out _))
        {
            return $"Item kind '{request.ItemKind}' is not recognized. Use 'stock', 'non_stock', or 'service'.";
        }

        if (request.DefaultSalesPrice is < 0) return "Default sales price cannot be negative.";
        if (request.DefaultPurchasePrice is < 0) return "Default purchase price cannot be negative.";

        return null;
    }

    public static InventoryItemUpsertRequest BuildItemUpsertRequest(
        Guid companyId,
        Guid userId,
        Guid? itemId,
        InventoryItemUpsertHttpRequest request)
    {
        TryParseItemKind(request.ItemKind, out var kind);
        var manageMethod = ParseManageInventoryMethodOrDefault(request.ManageInventoryMethod, kind);
        var costingMethod = ParseCostingMethodOrDefault(request.DefaultCostingMethod);
        var backorder = ParseBackorderModeOrDefault(request.BackorderMode);
        var lowStock = ParseLowStockActivityOrDefault(request.LowStockActivity);

        return new InventoryItemUpsertRequest(
            CompanyId: companyId,
            UserId: userId,
            ItemId: itemId,
            ItemCode: request.ItemCode.Trim(),
            Name: request.Name.Trim(),
            Description: request.Description,
            ItemKind: kind,
            StockUomCode: request.StockUomCode,
            ManageInventoryMethod: manageMethod,
            DefaultCostingMethod: costingMethod,
            BackorderMode: backorder,
            LowStockActivity: lowStock,
            DefaultInventoryAssetAccountId: request.DefaultInventoryAssetAccountId,
            DefaultCogsAccountId: request.DefaultCogsAccountId,
            DefaultWriteOffAccountId: request.DefaultWriteOffAccountId,
            DefaultPurchaseVarianceAccountId: request.DefaultPurchaseVarianceAccountId,
            DefaultSalesRevenueAccountId: request.DefaultSalesRevenueAccountId,
            DefaultSalesPrice: request.DefaultSalesPrice,
            DefaultPurchasePrice: request.DefaultPurchasePrice,
            DefaultSalesTaxCodeId: request.DefaultSalesTaxCodeId,
            DefaultPurchaseTaxCodeId: request.DefaultPurchaseTaxCodeId);
    }

    private static bool TryParseItemKind(string raw, out InventoryItemKind kind)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "stock":
                kind = InventoryItemKind.Stock;
                return true;
            case "non_stock":
            case "non-stock":
            case "nonstock":
                kind = InventoryItemKind.NonStock;
                return true;
            case "service":
                kind = InventoryItemKind.Service;
                return true;
            default:
                kind = InventoryItemKind.Service;
                return false;
        }
    }

    private static ManageInventoryMethod ParseManageInventoryMethodOrDefault(string? raw, InventoryItemKind kind) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "manage_stock" => ManageInventoryMethod.ManageStock,
            "manage_stock_by_sku" => ManageInventoryMethod.ManageStockBySku,
            "dont_manage_stock" => ManageInventoryMethod.DontManageStock,
            // Default: stock items track inventory; everything else does not.
            _ => kind == InventoryItemKind.Stock
                ? ManageInventoryMethod.ManageStock
                : ManageInventoryMethod.DontManageStock
        };

    private static InventoryCostingMethod ParseCostingMethodOrDefault(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "fifo" => InventoryCostingMethod.Fifo,
            "moving_average" => InventoryCostingMethod.MovingAverage,
            _ => InventoryCostingMethod.MovingAverage
        };

    private static InventoryBackorderMode ParseBackorderModeOrDefault(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "allow_negative" => InventoryBackorderMode.AllowNegative,
            "allow_negative_with_warning" => InventoryBackorderMode.AllowNegativeWithWarning,
            _ => InventoryBackorderMode.Disallow
        };

    private static InventoryLowStockActivity ParseLowStockActivityOrDefault(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "warn" => InventoryLowStockActivity.Warn,
            "block_outbound" => InventoryLowStockActivity.BlockOutbound,
            _ => InventoryLowStockActivity.Nothing
        };

    /// <summary>
    /// Wire-format projection of an item list row. Enum fields serialize
    /// to the same snake_case identifiers callers send on POST/PUT, so
    /// a response can be edited and round-tripped back to the API
    /// without a translation step on the Blazor side.
    /// </summary>
    public static object MapItemSummary(InventoryItemListRow row) => new
    {
        row.Id,
        row.CompanyId,
        row.ItemCode,
        row.Name,
        row.Description,
        ItemKind = FormatItemKind(row.ItemKind),
        row.StockUomCode,
        ManageInventoryMethod = FormatManageInventoryMethod(row.ManageInventoryMethod),
        DefaultCostingMethod = FormatCostingMethod(row.DefaultCostingMethod),
        BackorderMode = FormatBackorderMode(row.BackorderMode),
        LowStockActivity = FormatLowStockActivity(row.LowStockActivity),
        row.DefaultInventoryAssetAccountId,
        row.DefaultCogsAccountId,
        row.DefaultWriteOffAccountId,
        row.DefaultPurchaseVarianceAccountId,
        row.DefaultSalesRevenueAccountId,
        row.DefaultSalesPrice,
        row.DefaultPurchasePrice,
        row.DefaultSalesTaxCodeId,
        row.DefaultPurchaseTaxCodeId,
        row.IsActive,
        row.CreatedAt,
        row.UpdatedAt
    };

    private static string FormatItemKind(InventoryItemKind kind) => kind switch
    {
        InventoryItemKind.Stock => "stock",
        InventoryItemKind.NonStock => "non_stock",
        InventoryItemKind.Service => "service",
        _ => "service"
    };

    private static string FormatManageInventoryMethod(ManageInventoryMethod method) => method switch
    {
        ManageInventoryMethod.ManageStock => "manage_stock",
        ManageInventoryMethod.ManageStockBySku => "manage_stock_by_sku",
        _ => "dont_manage_stock"
    };

    private static string FormatCostingMethod(InventoryCostingMethod method) => method switch
    {
        InventoryCostingMethod.Fifo => "fifo",
        _ => "moving_average"
    };

    private static string FormatBackorderMode(InventoryBackorderMode mode) => mode switch
    {
        InventoryBackorderMode.AllowNegative => "allow_negative",
        InventoryBackorderMode.AllowNegativeWithWarning => "allow_negative_with_warning",
        _ => "disallow"
    };

    private static string FormatLowStockActivity(InventoryLowStockActivity activity) => activity switch
    {
        InventoryLowStockActivity.Warn => "warn",
        InventoryLowStockActivity.BlockOutbound => "block_outbound",
        _ => "nothing"
    };
}
