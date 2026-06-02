namespace Infrastructure.PostgreSQL.Inventory;

public static class InventoryAdjustmentAccountPolicy
{
    public static readonly string[] InventoryAssetRootTypes = ["asset"];

    public static readonly string[] AdjustmentOffsetRootTypes =
    [
        "revenue",
        "cost_of_sales",
        "expense"
    ];

    public static bool AllowsInventoryAssetRootType(string? rootType) =>
        AllowsRootType(rootType, InventoryAssetRootTypes);

    public static bool AllowsAdjustmentOffsetRootType(string? rootType) =>
        AllowsRootType(rootType, AdjustmentOffsetRootTypes);

    public static bool AllowsRootType(string? rootType, IReadOnlyCollection<string> allowedRootTypes)
    {
        if (string.IsNullOrWhiteSpace(rootType))
        {
            return false;
        }

        return allowedRootTypes.Contains(rootType.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public static string FormatAllowedRootTypes(IReadOnlyCollection<string> allowedRootTypes) =>
        string.Join(", ", allowedRootTypes.Select(static rootType => $"'{rootType}'"));
}
