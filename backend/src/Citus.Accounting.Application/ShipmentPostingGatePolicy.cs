using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Application;

public static class ShipmentPostingGatePolicy
{
    public static bool AllowsInvoicePost(string? matchStatus) =>
        string.Equals(matchStatus, "no_inventory_handoff", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(matchStatus, "fully_shipped", StringComparison.OrdinalIgnoreCase);

    public static string GetPostingGateLabel(InventoryInvoiceShipmentHandoffSummary? summary) =>
        GetPostingGateLabel(summary?.MatchStatus);

    public static string GetPostingGateLabel(InventoryInvoiceShipmentPostingGateSnapshot? snapshot) =>
        GetPostingGateLabel(snapshot?.MatchStatus);

    public static string GetPostingGateLabel(string? matchStatus) =>
        matchStatus switch
        {
            "no_shipment" => "Post on hold: no shipment yet",
            "partially_shipped" => "Post on hold: shipment still partial",
            "over_shipped" => "Post on hold: shipment mismatch",
            "fully_shipped" => "Post enabled",
            "no_inventory_handoff" => "Post enabled",
            _ => "Awaiting shipment review"
        };

    public static string GetPostingGateSummary(InventoryInvoiceShipmentHandoffSummary? summary) =>
        GetPostingGateSummary(summary?.MatchStatus, summary?.RemainingQuantity ?? 0m);

    public static string GetPostingGateSummary(InventoryInvoiceShipmentPostingGateSnapshot? snapshot) =>
        GetPostingGateSummary(snapshot?.MatchStatus, snapshot?.RemainingQuantity ?? 0m);

    public static string GetPostingGateSummary(string? matchStatus, decimal remainingQuantity) =>
        matchStatus switch
        {
            "no_shipment" => "Outbound inventory-grade invoice lines exist, but AR posting must wait until authoritative shipment truth is posted.",
            "partially_shipped" => $"AR posting stays on hold until the remaining {remainingQuantity:N2} outbound quantity is covered by shipment truth.",
            "over_shipped" => "AR posting stays on hold while anchored shipment truth exceeds the current invoice hand-off quantity. Review the fulfillment mismatch before formalizing AR truth.",
            "fully_shipped" => "Shipment-first matching fully covers the current invoice hand-off quantity, so the invoice can move into formal AR posting when you are ready.",
            "no_inventory_handoff" => "This invoice is not participating in shipment-first matching, so AR posting remains controlled only by the draft lifecycle lane.",
            _ => "Shipment-first matching truth has not been loaded yet."
        };

    public static string GetBlockedPostMessage(InventoryInvoiceShipmentHandoffSummary summary) =>
        GetBlockedPostMessage(summary.MatchStatus, summary.RemainingQuantity);

    public static string GetBlockedPostMessage(string? matchStatus, decimal remainingQuantity) =>
        matchStatus switch
        {
            "no_shipment" => "Invoice posting is on hold because outbound inventory-grade lines exist but no authoritative shipment has been posted yet.",
            "partially_shipped" => $"Invoice posting is on hold because shipment truth still has {remainingQuantity:N2} quantity outstanding against this invoice hand-off.",
            "over_shipped" => "Invoice posting is on hold because anchored shipments currently exceed the invoice hand-off quantity. Review the shipment mismatch before posting AR truth.",
            _ => "Invoice posting is on hold until shipment-first matching is resolved."
        };
}
