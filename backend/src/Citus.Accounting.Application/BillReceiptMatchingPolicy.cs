namespace Citus.Accounting.Application;

public static class BillReceiptMatchingPolicy
{
    public static BillReceiptMatchingComputation Compute(
        IReadOnlyList<BillReceiptMatchBillLineCandidate> billLines,
        IReadOnlyList<BillReceiptMatchReceiptLineCandidate> receiptLines)
    {
        ArgumentNullException.ThrowIfNull(billLines);
        ArgumentNullException.ThrowIfNull(receiptLines);

        var allocations = new List<BillReceiptMatchAllocation>();
        var groupedBillLines = billLines
            .GroupBy(static line => new BillReceiptMatchingAnchor(line.VendorId, line.ItemId, line.WarehouseId, NormalizeUom(line.UomCode)))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static line => ResolveBillPriority(line.BillStatus))
                    .ThenBy(static line => line.BillDate)
                    .ThenBy(static line => line.BillCreatedAt)
                    .ThenBy(static line => line.BillId)
                    .ThenBy(static line => line.LineNumber)
                    .ToArray());
        var groupedReceiptLines = receiptLines
            .GroupBy(static line => new BillReceiptMatchingAnchor(line.VendorId, line.ItemId, line.WarehouseId, NormalizeUom(line.UomCode)))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static line => line.ReceiptDate)
                    .ThenBy(static line => line.ReceiptCreatedAt)
                    .ThenBy(static line => line.ReceiptId)
                    .ThenBy(static line => line.LineNumber)
                    .ToArray());

        foreach (var (anchor, billsForAnchor) in groupedBillLines)
        {
            groupedReceiptLines.TryGetValue(anchor, out var receiptsForAnchor);
            var receiptRemainings = (receiptsForAnchor ?? Array.Empty<BillReceiptMatchReceiptLineCandidate>())
                .Select(static receipt => new BillReceiptReceiptRemaining(receipt, receipt.Quantity))
                .ToArray();

            foreach (var billLine in billsForAnchor)
            {
                var remainingBillQuantity = billLine.Quantity;

                foreach (var receiptRemaining in receiptRemainings)
                {
                    if (remainingBillQuantity <= 0m)
                    {
                        break;
                    }

                    if (receiptRemaining.RemainingQuantity <= 0m)
                    {
                        continue;
                    }

                    var matchedQuantity = Math.Min(remainingBillQuantity, receiptRemaining.RemainingQuantity);
                    if (matchedQuantity <= 0m)
                    {
                        continue;
                    }

                    receiptRemaining.RemainingQuantity = Round6(receiptRemaining.RemainingQuantity - matchedQuantity);
                    remainingBillQuantity = Round6(remainingBillQuantity - matchedQuantity);

                    allocations.Add(new BillReceiptMatchAllocation(
                        billLine.BillId,
                        billLine.LineNumber,
                        receiptRemaining.Receipt.ReceiptId,
                        receiptRemaining.Receipt.LineNumber,
                        matchedQuantity,
                        anchor));
                }
            }
        }

        var lineStatuses = new Dictionary<(Guid BillId, int LineNumber), BillReceiptMatchLineStatus>();
        foreach (var line in billLines)
        {
            var matchedQuantity = allocations
                .Where(allocation => allocation.BillId == line.BillId && allocation.BillLineNumber == line.LineNumber)
                .Sum(static allocation => allocation.MatchedQuantity);
            var remainingQuantity = ResolveRemainingQuantity(line.Quantity, matchedQuantity);
            var status = ResolveCoverageStatus(line.Quantity, matchedQuantity);
            lineStatuses[(line.BillId, line.LineNumber)] = new BillReceiptMatchLineStatus(matchedQuantity, remainingQuantity, status);
        }

        return new BillReceiptMatchingComputation(allocations, lineStatuses);
    }

    public static decimal ResolveRemainingQuantity(decimal basisQuantity, decimal matchedQuantity) =>
        Round6(Math.Max(0m, basisQuantity - matchedQuantity));

    public static string ResolveCoverageStatus(decimal basisQuantity, decimal matchedQuantity)
    {
        if (basisQuantity <= 0m)
        {
            return "no_receipt";
        }

        var normalizedMatchedQuantity = Round6(matchedQuantity);
        if (normalizedMatchedQuantity <= 0m)
        {
            return "no_receipt";
        }

        return normalizedMatchedQuantity < Round6(basisQuantity)
            ? "partially_covered"
            : "fully_covered";
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static string NormalizeUom(string uomCode) =>
        uomCode.Trim().ToUpperInvariant();

    private static int ResolveBillPriority(string? billStatus) =>
        billStatus?.Trim().ToLowerInvariant() switch
        {
            "posted" => 0,
            "submitted" => 1,
            "draft" => 2,
            _ => 3
        };
}

public sealed record BillReceiptMatchBillLineCandidate(
    Guid BillId,
    string BillStatus,
    int LineNumber,
    DateOnly BillDate,
    DateTimeOffset BillCreatedAt,
    Guid VendorId,
    Guid ItemId,
    Guid WarehouseId,
    string UomCode,
    decimal Quantity);

public sealed record BillReceiptMatchReceiptLineCandidate(
    Guid ReceiptId,
    int LineNumber,
    DateOnly ReceiptDate,
    DateTimeOffset ReceiptCreatedAt,
    Guid VendorId,
    Guid ItemId,
    Guid WarehouseId,
    string UomCode,
    decimal Quantity);

public sealed record BillReceiptMatchingAnchor(
    Guid VendorId,
    Guid ItemId,
    Guid WarehouseId,
    string UomCode);

public sealed record BillReceiptMatchAllocation(
    Guid BillId,
    int BillLineNumber,
    Guid ReceiptId,
    int ReceiptLineNumber,
    decimal MatchedQuantity,
    BillReceiptMatchingAnchor Anchor);

public sealed record BillReceiptMatchLineStatus(
    decimal MatchedQuantity,
    decimal RemainingQuantity,
    string MatchStatus);

public sealed record BillReceiptMatchingComputation(
    IReadOnlyList<BillReceiptMatchAllocation> Allocations,
    IReadOnlyDictionary<(Guid BillId, int LineNumber), BillReceiptMatchLineStatus> LineStatuses);

internal sealed class BillReceiptReceiptRemaining
{
    public BillReceiptReceiptRemaining(BillReceiptMatchReceiptLineCandidate receipt, decimal remainingQuantity)
    {
        Receipt = receipt;
        RemainingQuantity = remainingQuantity;
    }

    public BillReceiptMatchReceiptLineCandidate Receipt { get; }

    public decimal RemainingQuantity { get; set; }
}
