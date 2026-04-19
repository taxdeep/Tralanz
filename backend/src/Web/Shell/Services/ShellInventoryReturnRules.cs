using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public static class ShellInventoryReturnRules
{
    public static ShellInventoryReturnRuleResult ValidatePost(
        InventoryReturnReceivePostRequest? request,
        InventoryReturnReceiveHandoffSummary? handoffSummary)
    {
        if (request is null)
        {
            return Fail("missing_request", "Return receive input is required.");
        }

        if (handoffSummary is null)
        {
            return Fail("missing_handoff", "Choose a posted shipment anchor before posting return receive.");
        }

        if (request.CompanyId == Guid.Empty)
        {
            return Fail("missing_company", "Return receive must stay inside the active company.");
        }

        if (request.ShipmentDocumentId != handoffSummary.ShipmentDocumentId)
        {
            return Fail("shipment_mismatch", "Return receive must stay anchored to the selected shipment.");
        }

        if (request.CustomerId != handoffSummary.CustomerId)
        {
            return Fail("customer_mismatch", "Return receive must stay anchored to the shipment customer.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Fail("missing_lines", "At least one return line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0 || !seenLineNumbers.Add(line.LineNo))
            {
                return Fail("invalid_line_numbers", "Return line numbers must be positive and unique.");
            }

            var anchorLine = handoffSummary.LineSummaries.FirstOrDefault(candidate =>
                candidate.ItemId == line.ItemId &&
                candidate.WarehouseId == line.WarehouseId &&
                string.Equals(candidate.UomCode, line.UomCode, StringComparison.OrdinalIgnoreCase));

            if (anchorLine is null)
            {
                return Fail("invalid_anchor_line", "Return lines must stay inside the anchored shipment truth.");
            }

            if (line.Quantity <= 0m)
            {
                return Fail("invalid_quantity", "Return quantities must be positive.");
            }

            if (line.Quantity > anchorLine.RemainingReturnableQuantity)
            {
                return Fail("quantity_ceiling", "Return quantity cannot exceed the remaining returnable quantity on the shipment anchor.");
            }

            if (string.IsNullOrWhiteSpace(line.ConditionCode))
            {
                return Fail("missing_condition", "Each return line must include a condition code.");
            }

            if (string.IsNullOrWhiteSpace(line.ReturnReasonCode))
            {
                return Fail("missing_reason", "Each return line must include a return reason code.");
            }
        }

        return Success();
    }

    private static ShellInventoryReturnRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellInventoryReturnRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellInventoryReturnRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
