namespace Citus.Accounting.Application;

public static class BillReceiptDiscrepancyPolicy
{
    public static bool IsOpenDiscrepancyStatus(string? matchStatus) =>
        string.Equals(matchStatus, "no_receipt", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(matchStatus, "partially_covered", StringComparison.OrdinalIgnoreCase);

    public static string? ResolveDiscrepancyType(string? matchStatus) =>
        matchStatus?.Trim().ToLowerInvariant() switch
        {
            "no_receipt" => "missing_receipt_coverage",
            "partially_covered" => "partial_receipt_coverage",
            _ => null
        };

    public static string ResolveInvestigationStatus(string? discrepancyType) =>
        discrepancyType is null ? "none" : "open";

    public static string BuildDiscrepancySummary(
        string discrepancyType,
        string itemCode,
        string warehouseCode,
        decimal remainingQuantity,
        string uomCode) =>
        discrepancyType switch
        {
            "missing_receipt_coverage" => $"Posted receipt coverage is still missing for {itemCode} in {warehouseCode} ({remainingQuantity:N2} {uomCode} uncovered).",
            "partial_receipt_coverage" => $"Posted receipt coverage is still partial for {itemCode} in {warehouseCode} ({remainingQuantity:N2} {uomCode} uncovered).",
            _ => $"Receipt-first investigation remains open for {itemCode} in {warehouseCode}."
        };

    public static string? BuildBrowserSummary(int openDiscrepancyCount) =>
        openDiscrepancyCount <= 0
            ? null
            : openDiscrepancyCount == 1
                ? "1 inbound discrepancy lane is still open."
                : $"{openDiscrepancyCount} inbound discrepancy lanes are still open.";
}
