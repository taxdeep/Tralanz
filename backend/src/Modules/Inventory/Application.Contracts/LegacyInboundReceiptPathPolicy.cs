namespace Citus.Modules.Inventory.Application.Contracts;

public static class LegacyInboundReceiptPathPolicy
{
    public const string SourceModuleApBill = "ap_bill";
    public const string SourceModuleReceiptDocument = "receipt_document";
    public const string SourceModuleFirstClassReceipt = "first_class_receipt";

    public const string AllowedTransitionalFallback = "transitional_ap_bill_fallback";
    public const string AllowedNonBillSource = "non_bill_source";
    public const string BlockedFirstClassReceiptSource = "first_class_receipt_source";
    public const string BlockedMissingBillAnchor = "ap_bill_missing_source_document";
    public const string BlockedMissingBillSnapshot = "ap_bill_missing_policy_snapshot";
    public const string BlockedNoInventoryHandoff = "ap_bill_no_inventory_handoff";
    public const string BlockedFirstClassCoveragePresent = "first_class_receipt_coverage_present";
    public const string BlockedLineNotOnBill = "ap_bill_line_not_on_bill";
    public const string BlockedQuantityCeilingExceeded = "ap_bill_quantity_ceiling_exceeded";

    public static bool RequiresBillSnapshot(InventoryPurchaseReceiptPostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Equals(
            NormalizeSourceModule(request.SourceModule),
            SourceModuleApBill,
            StringComparison.OrdinalIgnoreCase) &&
            request.SourceDocumentId is { } sourceDocumentId &&
            sourceDocumentId != Guid.Empty;
    }

    public static LegacyInboundReceiptPathDecision Evaluate(
        InventoryPurchaseReceiptPostRequest request,
        LegacyInboundReceiptPathSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceModule = NormalizeSourceModule(request.SourceModule);
        if (sourceModule is SourceModuleReceiptDocument or SourceModuleFirstClassReceipt)
        {
            return Deny(
                BlockedFirstClassReceiptSource,
                "First-class receipt documents must activate inbound inventory through the receipt activation workflow, not the legacy purchase receipt path.");
        }

        if (sourceModule != SourceModuleApBill)
        {
            return Allow(
                AllowedNonBillSource,
                "This receipt is not an AP bill fallback, so first-class receipt retirement policy does not apply.");
        }

        if (request.SourceDocumentId is not { } billDocumentId || billDocumentId == Guid.Empty)
        {
            return Deny(
                BlockedMissingBillAnchor,
                "Legacy AP bill receipt fallback requires a source bill document id.");
        }

        if (snapshot is null || snapshot.BillDocumentId != billDocumentId)
        {
            return Deny(
                BlockedMissingBillSnapshot,
                "Legacy AP bill receipt fallback could not load receipt-first policy truth for the source bill.");
        }

        if (snapshot.BillInboundLineCount <= 0 || snapshot.BillInboundQuantity <= 0m)
        {
            return Deny(
                BlockedNoInventoryHandoff,
                "This bill has no inventory-grade inbound quantity, so it cannot create legacy inbound inventory truth.");
        }

        if (snapshot.HasFirstClassReceiptCoverage)
        {
            return Deny(
                BlockedFirstClassCoveragePresent,
                "This bill already has first-class receipt matching coverage. Legacy AP bill receipt fallback cannot create a second inbound quantity path.");
        }

        var snapshotLines = snapshot.Lines.ToDictionary(
            static line => new LegacyInboundReceiptPathLineKey(line.ItemId, line.WarehouseId, NormalizeUom(line.UomCode)),
            static line => line);
        var requestGroups = request.Lines
            .GroupBy(static line => new LegacyInboundReceiptPathLineKey(line.ItemId, line.WarehouseId, NormalizeUom(line.UomCode)))
            .Select(static group => new
            {
                Key = group.Key,
                Quantity = Round6(group.Sum(static line => line.Quantity))
            });

        foreach (var group in requestGroups)
        {
            if (!snapshotLines.TryGetValue(group.Key, out var sourceLine))
            {
                return Deny(
                    BlockedLineNotOnBill,
                    "Legacy AP bill receipt fallback can only receive item / warehouse / UOM anchors that exist on the source bill.");
            }

            if (group.Quantity > sourceLine.LegacyRemainingQuantity)
            {
                return Deny(
                    BlockedQuantityCeilingExceeded,
                    $"Legacy AP bill receipt fallback cannot exceed the remaining bill quantity for {sourceLine.ItemCode} / {sourceLine.WarehouseCode} / {sourceLine.UomCode}.");
            }
        }

        return Allow(
            AllowedTransitionalFallback,
            "Legacy AP bill receipt fallback is still allowed because no first-class receipt coverage exists and the request stays within the remaining bill quantity.");
    }

    private static LegacyInboundReceiptPathDecision Allow(string code, string message) =>
        new(true, code, message);

    private static LegacyInboundReceiptPathDecision Deny(string code, string message) =>
        new(false, code, message);

    private static string NormalizeSourceModule(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private static string NormalizeUom(string value) =>
        value.Trim().ToUpperInvariant();

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private readonly record struct LegacyInboundReceiptPathLineKey(
        Guid ItemId,
        Guid WarehouseId,
        string UomCode);
}

public sealed record class LegacyInboundReceiptPathDecision(
    bool IsAllowed,
    string Code,
    string Message);

public sealed record class LegacyInboundReceiptPathSnapshot(
    Guid BillDocumentId,
    int BillInboundLineCount,
    decimal BillInboundQuantity,
    int LegacyReceiptCount,
    decimal LegacyReceivedQuantity,
    int FirstClassCoverageCount,
    decimal FirstClassCoveredQuantity,
    IReadOnlyList<LegacyInboundReceiptPathLineSnapshot> Lines)
{
    public bool HasFirstClassReceiptCoverage =>
        FirstClassCoverageCount > 0 ||
        FirstClassCoveredQuantity > 0m ||
        Lines.Any(static line => line.FirstClassCoveredQuantity > 0m);
}

public sealed record class LegacyInboundReceiptPathLineSnapshot(
    Guid ItemId,
    string ItemCode,
    Guid WarehouseId,
    string WarehouseCode,
    string UomCode,
    decimal BillQuantity,
    decimal LegacyReceivedQuantity,
    decimal FirstClassCoveredQuantity)
{
    public decimal LegacyRemainingQuantity =>
        Math.Round(Math.Max(0m, BillQuantity - LegacyReceivedQuantity), 6, MidpointRounding.ToEven);
}
