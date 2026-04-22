using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public static class ShellInventoryTransferRules
{
    public static ShellInventoryTransferRuleResult ValidateUpsert(
        InventoryTransferUpsertRequest? request,
        InventoryTransferDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Transfer input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory transfer context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Transfer must stay inside the active company.");
        }

        if (request.SourceWarehouseId == Guid.Empty || request.DestinationWarehouseId == Guid.Empty)
        {
            return Fail("missing_warehouses", "Choose both source and destination warehouses.");
        }

        if (request.SourceWarehouseId == request.DestinationWarehouseId)
        {
            return Fail("same_warehouse", "Source and destination warehouses must be different.");
        }

        if (!dashboard.ActiveWarehouses.Any(warehouse => warehouse.Id == request.SourceWarehouseId) ||
            !dashboard.ActiveWarehouses.Any(warehouse => warehouse.Id == request.DestinationWarehouseId))
        {
            return Fail("invalid_warehouse", "Transfer must use active warehouses.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Fail("missing_lines", "At least one transfer line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0 || !seenLineNumbers.Add(line.LineNo))
            {
                return Fail("invalid_line_numbers", "Transfer line numbers must be positive and unique.");
            }

            var item = dashboard.ActiveItems.FirstOrDefault(item => item.Id == line.ItemId);
            if (item is null)
            {
                return Fail("invalid_item", "Each transfer line must select an active stock item.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                return Fail("missing_uom", "Each transfer line must include a UOM code.");
            }

            if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Fail("uom_mismatch", $"Transfer line UOM must match the stock UOM for '{item.Name}'.");
            }

            if (line.Quantity <= 0)
            {
                return Fail("invalid_quantity", "Transfer quantities must be positive.");
            }
        }

        return Success();
    }

    public static ShellInventoryTransferRuleResult ValidateSubmit(
        Guid companyId,
        Guid transferId,
        InventoryTransferDashboard? dashboard)
    {
        var transfer = FindTransfer(companyId, transferId, dashboard, out var precheckFailure);
        if (precheckFailure is not null)
        {
            return precheckFailure;
        }

        if (!string.Equals(transfer!.Status, "draft", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("invalid_submit_status", "Only draft transfers can be submitted.");
        }

        if (transfer.LineCount <= 0 || transfer.TotalQuantity <= 0)
        {
            return Fail("empty_transfer", "Transfer must keep at least one positive-quantity line before submission.");
        }

        return Success();
    }

    public static ShellInventoryTransferRuleResult ValidateShip(
        Guid companyId,
        Guid transferId,
        DateOnly postingDate,
        InventoryTransferDashboard? dashboard)
    {
        var transfer = FindTransfer(companyId, transferId, dashboard, out var precheckFailure);
        if (precheckFailure is not null)
        {
            return precheckFailure;
        }

        if (!string.Equals(transfer!.Status, "submitted", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("invalid_ship_status", "Only submitted transfers can be shipped.");
        }

        if (transfer.SubmittedAt is null)
        {
            return Fail("missing_submit_stamp", "Submitted transfer is missing its submit timestamp.");
        }

        return Success();
    }

    public static ShellInventoryTransferRuleResult ValidateReceive(
        Guid companyId,
        Guid transferId,
        DateOnly postingDate,
        InventoryTransferDashboard? dashboard)
    {
        var transfer = FindTransfer(companyId, transferId, dashboard, out var precheckFailure);
        if (precheckFailure is not null)
        {
            return precheckFailure;
        }

        if (!string.Equals(transfer!.Status, "shipped", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("invalid_receive_status", "Only shipped transfers can be received.");
        }

        if (transfer.ShippedAt is null)
        {
            return Fail("missing_ship_stamp", "Shipped transfer is missing its ship timestamp.");
        }

        if (postingDate < DateOnly.FromDateTime(transfer.ShippedAt.Value.UtcDateTime.Date))
        {
            return Fail("receive_before_ship", "Transfer receive date cannot be earlier than the shipped date.");
        }

        return Success();
    }

    private static InventoryTransferSummary? FindTransfer(
        Guid companyId,
        Guid transferId,
        InventoryTransferDashboard? dashboard,
        out ShellInventoryTransferRuleResult? precheckFailure)
    {
        precheckFailure = null;

        if (dashboard is null)
        {
            precheckFailure = Fail("missing_dashboard", "Inventory transfer context is unavailable.");
            return null;
        }

        if (companyId != dashboard.CompanyId)
        {
            precheckFailure = Fail("company_mismatch", "Transfer must stay inside the active company.");
            return null;
        }

        var transfer = dashboard.RecentTransfers.FirstOrDefault(candidate => candidate.TransferId == transferId);
        if (transfer is null)
        {
            precheckFailure = Fail("missing_transfer", "Transfer could not be found in the current dashboard context.");
            return null;
        }

        return transfer;
    }

    private static ShellInventoryTransferRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellInventoryTransferRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellInventoryTransferRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
