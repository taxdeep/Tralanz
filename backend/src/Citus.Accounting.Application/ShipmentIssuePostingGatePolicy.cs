using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Application;

public static class ShipmentIssuePostingGatePolicy
{
    public static string GetShipmentFirstStatusLabel(bool hasOutboundInventoryHandoff) =>
        hasOutboundInventoryHandoff
            ? "Shipment-first bridge in progress"
            : "Invoice-only draft lane";

    public static string GetShipmentFirstStatusSummary(bool hasOutboundInventoryHandoff) =>
        hasOutboundInventoryHandoff
            ? "Outbound inventory-grade invoice lines are present. Shipment and Sales Issue still remain the authoritative physical truth, so this hand-off is only the first bridge seam."
            : "This invoice is not participating in outbound inventory hand-off. Posting still follows the existing AR draft lane until shipment-first bridging deepens.";

    public static bool AllowsInvoicePost(string? matchStatus) =>
        string.Equals(matchStatus, "no_inventory_handoff", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(matchStatus, "fully_issued", StringComparison.OrdinalIgnoreCase);

    public static string GetPostingGateLabel(InventoryInvoiceIssueHandoffSummary? summary) =>
        GetPostingGateLabel(summary?.MatchStatus);

    public static string GetPostingGateLabel(InventoryInvoiceIssuePostingGateSnapshot? snapshot) =>
        GetPostingGateLabel(snapshot?.MatchStatus);

    public static string GetPostingGateLabel(string? matchStatus) =>
        matchStatus switch
        {
            "no_issue" => "Post on hold: no sales issue yet",
            "partially_issued" => "Post on hold: issue still partial",
            "over_issued" => "Post on hold: issue mismatch",
            "fully_issued" => "Post enabled",
            "no_inventory_handoff" => "Post enabled",
            _ => "Awaiting issue review"
        };

    public static string GetPostingGateSummary(InventoryInvoiceIssueHandoffSummary? summary) =>
        GetPostingGateSummary(summary?.MatchStatus, summary?.RemainingQuantity ?? 0m);

    public static string GetPostingGateSummary(InventoryInvoiceIssuePostingGateSnapshot? snapshot) =>
        GetPostingGateSummary(snapshot?.MatchStatus, snapshot?.RemainingQuantity ?? 0m);

    public static string GetPostingGateSummary(string? matchStatus, decimal remainingQuantity) =>
        matchStatus switch
        {
            "no_issue" => "Outbound inventory-grade invoice lines exist, but AR posting must wait until authoritative sales issue truth is posted. Shipment-first bridging is still in progress, so issue coverage is the current control seam.",
            "partially_issued" => $"AR posting stays on hold until the remaining {remainingQuantity:N2} outbound quantity is covered by authoritative sales issue truth.",
            "over_issued" => "AR posting stays on hold while anchored sales issue truth exceeds the current invoice hand-off quantity. Review the outbound mismatch before formalizing AR truth.",
            "fully_issued" => "Sales issue truth fully covers the current invoice hand-off quantity, so the invoice can move into formal AR posting when you are ready.",
            "no_inventory_handoff" => "This invoice is not participating in shipment-first bridging, so AR posting remains controlled only by the draft lifecycle lane.",
            _ => "Shipment-first issue truth has not been loaded yet."
        };

    public static string GetBlockedPostMessage(InventoryInvoiceIssueHandoffSummary summary) =>
        GetBlockedPostMessage(summary.MatchStatus, summary.RemainingQuantity);

    public static string GetBlockedPostMessage(string? matchStatus, decimal remainingQuantity) =>
        matchStatus switch
        {
            "no_issue" => "Invoice posting is on hold because outbound inventory-grade lines exist but no authoritative sales issue has been posted yet.",
            "partially_issued" => $"Invoice posting is on hold because sales issue truth still has {remainingQuantity:N2} quantity outstanding against this invoice hand-off.",
            "over_issued" => "Invoice posting is on hold because anchored sales issues currently exceed the invoice hand-off quantity. Review the outbound mismatch before posting AR truth.",
            _ => "Invoice posting is on hold until shipment-first issue matching is resolved."
        };
}
