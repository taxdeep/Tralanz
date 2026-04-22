using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public static class ShellInventoryShipmentRules
{
    public static ShellInventoryShipmentRuleResult ValidatePost(
        InventoryShipmentPostRequest? request,
        InventoryShipmentDashboard? dashboard,
        ShellCounterpartyOnboardingSummary? counterparties)
    {
        if (request is null)
        {
            return Fail("missing_request", "Shipment input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory shipment context is unavailable.");
        }

        if (counterparties is null)
        {
            return Fail("missing_counterparties", "Customer context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Shipment must stay inside the active company.");
        }

        if (!counterparties.ActiveCustomers.Any(customer => customer.Id == request.CustomerId))
        {
            return Fail("missing_customer", "Choose an active customer before posting a shipment.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Fail("missing_lines", "At least one shipment line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0 || !seenLineNumbers.Add(line.LineNo))
            {
                return Fail("invalid_line_numbers", "Shipment line numbers must be positive and unique.");
            }

            var item = dashboard.ActiveItems.FirstOrDefault(item => item.Id == line.ItemId);
            if (item is null)
            {
                return Fail("invalid_item", "Each shipment line must select an active stock item.");
            }

            if (!dashboard.ActiveWarehouses.Any(warehouse => warehouse.Id == line.WarehouseId))
            {
                return Fail("invalid_warehouse", "Each shipment line must select an active warehouse.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                return Fail("missing_uom", "Each shipment line must include a UOM code.");
            }

            if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Fail("uom_mismatch", $"Shipment line UOM must match the stock UOM for '{item.Name}'.");
            }

            if (line.Quantity <= 0m)
            {
                return Fail("invalid_quantity", "Shipment quantities must be positive.");
            }
        }

        return Success();
    }

    private static ShellInventoryShipmentRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellInventoryShipmentRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellInventoryShipmentRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
