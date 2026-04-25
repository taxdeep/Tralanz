using Modules.GL.JournalEntry;

namespace Web.Shell.Services;

public static class ShellProductServiceCatalogRules
{
    public const string CatalogTypeProduct = "product";
    public const string CatalogTypeService = "service";
    public const string CatalogTypeNonInventoryProduct = "non_inventory_product";

    public static ShellProductServiceCatalogRuleResult Validate(
        ShellProductServiceUpsertRequest? request,
        IReadOnlyList<ShellManagedProductServiceSummary> existingItems,
        IReadOnlyList<JournalEntryAccountOption> salesRevenueAccountOptions,
        IReadOnlyList<JournalEntryAccountOption> purchaseExpenseAccountOptions,
        IReadOnlyList<ShellTaxCodeLookupOption> salesTaxCodeOptions,
        IReadOnlyList<ShellTaxCodeLookupOption> purchaseTaxCodeOptions)
    {
        if (request is null)
        {
            return Fail("missing_request", "Product or service input is required.");
        }

        var normalizedName = request.Name.Trim();
        var normalizedCatalogType = request.CatalogType.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Fail("missing_name", "Product or service name is required.");
        }

        if (!IsSupportedCatalogType(normalizedCatalogType))
        {
            return Fail("invalid_catalog_type", "Catalog type must be Services, Product, or Non-Inventory Product.");
        }

        if (existingItems.Any(
                item => item.Id != request.Id &&
                        string.Equals(item.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("duplicate_name", "A product or service with the same company-scoped name already exists.");
        }

        if (request.SalesRevenueAccountId.HasValue &&
            !salesRevenueAccountOptions.Any(option => option.AccountId == request.SalesRevenueAccountId.Value))
        {
            return Fail("invalid_sales_account", "The selected sales revenue account is not available in the current company context.");
        }

        if (request.PurchaseExpenseAccountId.HasValue &&
            !purchaseExpenseAccountOptions.Any(option => option.AccountId == request.PurchaseExpenseAccountId.Value))
        {
            return Fail("invalid_purchase_account", "The selected purchase expense account is not available in the current company context.");
        }

        if (request.DefaultSalesTaxCodeId.HasValue &&
            !salesTaxCodeOptions.Any(option => option.Id == request.DefaultSalesTaxCodeId.Value))
        {
            return Fail("invalid_sales_tax_code", "The selected sales tax code is not active for the current company.");
        }

        if (request.DefaultPurchaseTaxCodeId.HasValue &&
            !purchaseTaxCodeOptions.Any(option => option.Id == request.DefaultPurchaseTaxCodeId.Value))
        {
            return Fail("invalid_purchase_tax_code", "The selected purchase tax code is not active for the current company.");
        }

        return Success();
    }

    public static bool IsSupportedCatalogType(string? value) =>
        string.Equals(value, CatalogTypeProduct, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, CatalogTypeService, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, CatalogTypeNonInventoryProduct, StringComparison.OrdinalIgnoreCase);

    public static bool EntersInventoryManagement(string? value) =>
        string.Equals(value, CatalogTypeProduct, StringComparison.OrdinalIgnoreCase);

    public static string GetCatalogTypeLabel(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            CatalogTypeProduct => "Product",
            CatalogTypeNonInventoryProduct => "Non-Inventory Product",
            _ => "Services"
        };

    private static ShellProductServiceCatalogRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellProductServiceCatalogRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellProductServiceCatalogRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
