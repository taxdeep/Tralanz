using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Application;

public static class BillReceiptPostingGatePolicy
{
    public static bool AllowsBillPost(string? matchStatus) =>
        string.Equals(matchStatus, "no_inventory_handoff", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(matchStatus, "fully_receipted", StringComparison.OrdinalIgnoreCase);

    public static string GetPostingGateLabel(InventoryBillReceiptHandoffSummary? summary) =>
        GetPostingGateLabel(summary?.MatchStatus);

    public static string GetPostingGateLabel(InventoryBillReceiptPostingGateSnapshot? snapshot) =>
        GetPostingGateLabel(snapshot?.MatchStatus);

    public static string GetPostingGateLabel(string? matchStatus) =>
        matchStatus switch
        {
            "no_receipt" => "Post on hold: no receipt yet",
            "partially_receipted" => "Post on hold: receipt still partial",
            "over_receipted" => "Post on hold: receipt mismatch",
            "fully_receipted" => "Post enabled",
            "no_inventory_handoff" => "Post enabled",
            _ => "Awaiting receipt review"
        };

    public static string GetPostingGateSummary(InventoryBillReceiptHandoffSummary? summary) =>
        GetPostingGateSummary(summary?.MatchStatus, summary?.RemainingQuantity ?? 0m);

    public static string GetPostingGateSummary(InventoryBillReceiptPostingGateSnapshot? snapshot) =>
        GetPostingGateSummary(snapshot?.MatchStatus, snapshot?.RemainingQuantity ?? 0m);

    public static string GetPostingGateSummary(string? matchStatus, decimal remainingQuantity) =>
        matchStatus switch
        {
            "no_receipt" => "Inventory-grade inbound lines exist, but AP posting must wait until at least one authoritative purchase receipt is posted.",
            "partially_receipted" => $"AP posting stays on hold until the remaining {remainingQuantity:N2} inbound quantity is covered by receipt truth.",
            "over_receipted" => "AP posting stays on hold while receipt truth exceeds the current bill hand-off quantity. Review the mismatch before formalizing AP truth.",
            "fully_receipted" => "Receipt-first matching fully covers the current bill hand-off quantity, so the bill can move into formal AP posting when you are ready.",
            "no_inventory_handoff" => "This bill is not participating in receipt-first matching, so AP posting remains controlled only by the draft lifecycle lane.",
            _ => "Receipt-first matching truth has not been loaded yet."
        };

    public static string GetBlockedPostMessage(InventoryBillReceiptHandoffSummary summary) =>
        GetBlockedPostMessage(summary.MatchStatus, summary.RemainingQuantity);

    public static string GetBlockedPostMessage(string? matchStatus, decimal remainingQuantity) =>
        matchStatus switch
        {
            "no_receipt" => "Bill posting is on hold because inventory-grade inbound lines exist but no authoritative purchase receipt has been posted yet.",
            "partially_receipted" => $"Bill posting is on hold because receipt truth still has {remainingQuantity:N2} quantity outstanding against this bill hand-off.",
            "over_receipted" => "Bill posting is on hold because anchored purchase receipts currently exceed the bill hand-off quantity. Review the receipt mismatch before posting AP truth.",
            _ => "Bill posting is on hold until receipt-first matching is resolved."
        };
}
