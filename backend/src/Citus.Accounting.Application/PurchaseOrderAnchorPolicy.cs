using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application;

public static class PurchaseOrderAnchorPolicy
{
    public const string AllowedAnchorStatus = PurchaseOrderDocumentStatuses.Issued;

    public static bool AllowsNewAnchor(string? purchaseOrderStatus) =>
        string.Equals(
            PurchaseOrderDocumentStatuses.Normalize(purchaseOrderStatus),
            AllowedAnchorStatus,
            StringComparison.Ordinal);

    public static void EnsureAllowsNewAnchor(string? purchaseOrderStatus)
    {
        if (!AllowsNewAnchor(purchaseOrderStatus))
        {
            throw new InvalidOperationException(
                "PO anchors require an issued purchase order. Draft, closed, and cancelled purchase orders cannot receive new receipt or bill anchors.");
        }
    }

    public static string BuildAnchorStatusSummary(string? purchaseOrderStatus) =>
        AllowsNewAnchor(purchaseOrderStatus)
            ? "Issued PO lines can receive explicit Receipt/Bill anchors."
            : "Draft, closed, and cancelled PO lines are closed to new Receipt/Bill anchors.";
}

public static class PurchaseOrderQuantityDiscrepancyPolicy
{
    public const string OverReceived = "over_received";
    public const string OverBilled = "over_billed";
    public const string BilledAheadOfReceived = "billed_ahead_of_received";

    public static bool IsDiscrepancyStatus(string? quantityStatus) =>
        string.Equals(quantityStatus, PurchaseOrderThreeQuantityStatusPolicy.OverReceived, StringComparison.Ordinal) ||
        string.Equals(quantityStatus, PurchaseOrderThreeQuantityStatusPolicy.OverBilled, StringComparison.Ordinal) ||
        string.Equals(quantityStatus, PurchaseOrderThreeQuantityStatusPolicy.BilledAheadOfReceived, StringComparison.Ordinal);

    public static string? ResolveDiscrepancyType(PurchaseOrderLineThreeQuantitySummary line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.QuantityStatus switch
        {
            PurchaseOrderThreeQuantityStatusPolicy.OverReceived => OverReceived,
            PurchaseOrderThreeQuantityStatusPolicy.OverBilled => OverBilled,
            PurchaseOrderThreeQuantityStatusPolicy.BilledAheadOfReceived => BilledAheadOfReceived,
            _ => null
        };
    }

    public static string BuildDiscrepancySummary(
        string discrepancyType,
        decimal orderedQuantity,
        decimal receivedQuantity,
        decimal billedQuantity,
        string uomCode) =>
        discrepancyType switch
        {
            OverReceived =>
                $"Received {receivedQuantity:0.######} {uomCode} against ordered {orderedQuantity:0.######} {uomCode}. New PO-anchored receipts are blocked until the over-receipt is investigated.",
            OverBilled =>
                $"Billed {billedQuantity:0.######} {uomCode} against ordered {orderedQuantity:0.######} {uomCode}. New PO-anchored bills are blocked until the over-bill is investigated.",
            BilledAheadOfReceived =>
                $"Billed {billedQuantity:0.######} {uomCode} while only {receivedQuantity:0.######} {uomCode} has posted receipt truth. Bill posting is blocked until receipt coverage catches up.",
            _ => "PO quantity discrepancy requires investigation."
        };
}
