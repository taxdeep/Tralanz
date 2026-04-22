using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;

namespace Web.Shell.Services;

public static class ShellInventoryAdjustmentRules
{
    public static ShellInventoryAdjustmentRuleResult ValidateWriteOffRequest(
        InventoryWriteOffRequestPostRequest? request,
        InventoryAdjustmentDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Write-off request input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory adjustment context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Write-off request must stay inside the active company.");
        }

        if (!dashboard.ActiveWarehouses.Any(warehouse => warehouse.Id == request.WarehouseId))
        {
            return Fail("invalid_warehouse", "Choose an active warehouse before requesting write-off.");
        }

        return ValidateLines(
            request.Lines,
            dashboard,
            InventoryAdjustmentKind.WriteOff,
            "write-off request");
    }

    public static ShellInventoryAdjustmentRuleResult ValidateWriteOffApproval(
        InventoryWriteOffApprovePostRequest? request,
        InventoryAdjustmentDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Write-off approval input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory adjustment context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Write-off approval must stay inside the active company.");
        }

        var summary = dashboard.RecentAdjustments.FirstOrDefault(item => item.DocumentId == request.DocumentId);
        if (summary is null)
        {
            return Fail("missing_document", "The selected write-off request could not be found.");
        }

        if (summary.AdjustmentKind != InventoryAdjustmentKind.WriteOff)
        {
            return Fail("invalid_document_kind", "Only write-off requests can be approved from this control lane.");
        }

        if (!string.Equals(summary.Status, "submitted", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("invalid_document_status", "Only submitted write-off requests can be approved.");
        }

        return Success();
    }

    public static ShellInventoryAdjustmentRuleResult ValidateWriteOffPost(
        InventoryWriteOffApprovePostRequest? request,
        InventoryAdjustmentDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Write-off post input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory adjustment context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Write-off post must stay inside the active company.");
        }

        var summary = dashboard.RecentAdjustments.FirstOrDefault(item => item.DocumentId == request.DocumentId);
        if (summary is null)
        {
            return Fail("missing_document", "The selected write-off request could not be found.");
        }

        if (summary.AdjustmentKind != InventoryAdjustmentKind.WriteOff)
        {
            return Fail("invalid_document_kind", "Only write-off requests can be posted from this control lane.");
        }

        if (!string.Equals(summary.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("approval_required", "Only approved write-off requests can be posted.");
        }

        return Success();
    }

    public static ShellInventoryAdjustmentRuleResult ValidatePost(
        InventoryAdjustmentPostRequest? request,
        InventoryAdjustmentDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Inventory adjustment input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory adjustment context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Inventory adjustment must stay inside the active company.");
        }

        if (!dashboard.ActiveWarehouses.Any(warehouse => warehouse.Id == request.WarehouseId))
        {
            return Fail("invalid_warehouse", "Choose an active warehouse before posting inventory adjustment.");
        }

        if (request.AdjustmentKind == InventoryAdjustmentKind.WriteOff &&
            dashboard.CostingPolicy?.RequireWriteOffApproval == true)
        {
            return Fail("writeoff_requires_approval", "Write-off is blocked because company policy currently requires approval before posting.");
        }

        return ValidateLines(
            request.Lines,
            dashboard,
            request.AdjustmentKind,
            "inventory adjustment");
    }

    private static ShellInventoryAdjustmentRuleResult ValidateLines(
        IReadOnlyList<InventoryAdjustmentLineInput>? lines,
        InventoryAdjustmentDashboard dashboard,
        InventoryAdjustmentKind adjustmentKind,
        string contextLabel)
    {
        if (lines is null || lines.Count == 0)
        {
            return Fail("missing_lines", $"At least one {contextLabel} line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in lines)
        {
            if (line.LineNo <= 0 || !seenLineNumbers.Add(line.LineNo))
            {
                return Fail("invalid_line_numbers", $"{Capitalize(contextLabel)} line numbers must be positive and unique.");
            }

            var item = dashboard.ActiveItems.FirstOrDefault(item => item.Id == line.ItemId);
            if (item is null)
            {
                return Fail("invalid_item", $"Each {contextLabel} line must select an active stock item.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                return Fail("missing_uom", $"Each {contextLabel} line must include a UOM code.");
            }

            if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Fail("uom_mismatch", $"{Capitalize(contextLabel)} line UOM must match the stock UOM for '{item.Name}'.");
            }

            if (line.Quantity <= 0)
            {
                return Fail("invalid_quantity", $"{Capitalize(contextLabel)} quantities must be positive.");
            }

            if (adjustmentKind == InventoryAdjustmentKind.Gain &&
                (!line.UnitCostBase.HasValue || line.UnitCostBase.Value < 0))
            {
                return Fail("missing_unit_cost", "Inventory gain lines must include a non-negative unit cost.");
            }
        }

        return Success();
    }

    private static ShellInventoryAdjustmentRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellInventoryAdjustmentRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };

    private static string Capitalize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];
}

public sealed record class ShellInventoryAdjustmentRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
