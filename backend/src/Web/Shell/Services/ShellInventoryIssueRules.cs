using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public static class ShellInventoryIssueRules
{
    public static ShellInventoryIssueRuleResult ValidatePost(
        InventorySalesIssuePostRequest? request,
        InventorySalesIssueDashboard? dashboard,
        ShellCounterpartyOnboardingSummary? counterparties)
    {
        if (request is null)
        {
            return Fail("missing_request", "Sales issue input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory sales issue context is unavailable.");
        }

        if (counterparties is null)
        {
            return Fail("missing_counterparties", "Customer context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Sales issue must stay inside the active company.");
        }

        if (!counterparties.ActiveCustomers.Any(customer => customer.Id == request.CustomerId))
        {
            return Fail("missing_customer", "Choose an active customer before posting a sales issue.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Fail("missing_lines", "At least one sales issue line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0 || !seenLineNumbers.Add(line.LineNo))
            {
                return Fail("invalid_line_numbers", "Sales issue line numbers must be positive and unique.");
            }

            var item = dashboard.ActiveItems.FirstOrDefault(item => item.Id == line.ItemId);
            if (item is null)
            {
                return Fail("invalid_item", "Each sales issue line must select an active stock item.");
            }

            if (!dashboard.ActiveWarehouses.Any(warehouse => warehouse.Id == line.WarehouseId))
            {
                return Fail("invalid_warehouse", "Each sales issue line must select an active warehouse.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                return Fail("missing_uom", "Each sales issue line must include a UOM code.");
            }

            if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Fail("uom_mismatch", $"Sales issue line UOM must match the stock UOM for '{item.Name}'.");
            }

            if (line.Quantity <= 0)
            {
                return Fail("invalid_quantity", "Sales issue quantities must be positive.");
            }
        }

        return Success();
    }

    private static ShellInventoryIssueRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellInventoryIssueRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellInventoryIssueRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
