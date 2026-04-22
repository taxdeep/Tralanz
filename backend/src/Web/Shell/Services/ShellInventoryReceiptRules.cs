using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public static class ShellInventoryReceiptRules
{
    public static ShellInventoryReceiptRuleResult ValidatePost(
        InventoryPurchaseReceiptPostRequest? request,
        InventoryPurchaseReceiptDashboard? dashboard,
        ShellCounterpartyOnboardingSummary? counterparties)
    {
        if (request is null)
        {
            return Fail("missing_request", "Purchase receipt input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory receipt context is unavailable.");
        }

        if (counterparties is null)
        {
            return Fail("missing_counterparties", "Vendor context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Purchase receipt must stay inside the active company.");
        }

        if (!counterparties.ActiveVendors.Any(vendor => vendor.Id == request.VendorId))
        {
            return Fail("missing_vendor", "Choose an active vendor before posting a purchase receipt.");
        }

        var normalizedCurrencyCode = request.TransactionCurrencyCode.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedCurrencyCode))
        {
            return Fail("missing_currency", "Transaction currency is required.");
        }

        if (!counterparties.MultiCurrencyEnabled &&
            !string.Equals(normalizedCurrencyCode, counterparties.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("base_currency_required", "This company is in strict single-currency mode. Purchase receipts must use the base currency.");
        }

        if (counterparties.MultiCurrencyEnabled &&
            counterparties.EnabledCurrencies.All(currency =>
                !string.Equals(currency.Code, normalizedCurrencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("unsupported_currency", "The selected currency is not enabled for this company.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Fail("missing_lines", "At least one purchase receipt line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0 || !seenLineNumbers.Add(line.LineNo))
            {
                return Fail("invalid_line_numbers", "Purchase receipt line numbers must be positive and unique.");
            }

            var item = dashboard.ActiveItems.FirstOrDefault(item => item.Id == line.ItemId);
            if (item is null)
            {
                return Fail("invalid_item", "Each purchase receipt line must select an active stock item.");
            }

            if (!dashboard.ActiveWarehouses.Any(warehouse => warehouse.Id == line.WarehouseId))
            {
                return Fail("invalid_warehouse", "Each purchase receipt line must select an active warehouse.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                return Fail("missing_uom", "Each purchase receipt line must include a UOM code.");
            }

            if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Fail("uom_mismatch", $"Purchase receipt line UOM must match the stock UOM for '{item.Name}'.");
            }

            if (line.Quantity <= 0)
            {
                return Fail("invalid_quantity", "Purchase receipt quantities must be positive.");
            }

            if (line.UnitCostTx < 0)
            {
                return Fail("invalid_unit_cost", "Purchase receipt unit cost cannot be negative.");
            }
        }

        if (request.FxRateToBase <= 0)
        {
            return Fail("invalid_fx_rate", "FX rate to base must be greater than zero.");
        }

        return Success();
    }

    private static ShellInventoryReceiptRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellInventoryReceiptRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellInventoryReceiptRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
