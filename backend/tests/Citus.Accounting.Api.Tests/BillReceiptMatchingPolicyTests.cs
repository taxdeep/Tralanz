using Citus.Accounting.Application;

namespace Citus.Accounting.Api.Tests;

public sealed class BillReceiptMatchingPolicyTests
{
    [Fact]
    public void Compute_supports_partial_matching_across_multiple_receipts()
    {
        var billId = Guid.NewGuid();
        var receiptAId = Guid.NewGuid();
        var receiptBId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();

        var computation = BillReceiptMatchingPolicy.Compute(
            new[]
            {
                new BillReceiptMatchBillLineCandidate(
                    billId,
                    "submitted",
                    1,
                    new DateOnly(2026, 4, 19),
                    new DateTimeOffset(2026, 4, 19, 8, 0, 0, TimeSpan.Zero),
                    vendorId,
                    itemId,
                    warehouseId,
                    "ea",
                    10m)
            },
            new[]
            {
                new BillReceiptMatchReceiptLineCandidate(
                    receiptAId,
                    1,
                    new DateOnly(2026, 4, 18),
                    new DateTimeOffset(2026, 4, 18, 8, 0, 0, TimeSpan.Zero),
                    vendorId,
                    itemId,
                    warehouseId,
                    "EA",
                    4m),
                new BillReceiptMatchReceiptLineCandidate(
                    receiptBId,
                    1,
                    new DateOnly(2026, 4, 19),
                    new DateTimeOffset(2026, 4, 19, 7, 0, 0, TimeSpan.Zero),
                    vendorId,
                    itemId,
                    warehouseId,
                    "EA",
                    3m)
            });

        Assert.Equal(2, computation.Allocations.Count);
        Assert.Equal(7m, computation.Allocations.Sum(static allocation => allocation.MatchedQuantity));
        Assert.Equal("partially_covered", computation.LineStatuses[(billId, 1)].MatchStatus);
        Assert.Equal(3m, computation.LineStatuses[(billId, 1)].RemainingQuantity);
    }

    [Fact]
    public void Compute_does_not_over_allocate_a_receipt_line_and_prioritizes_formal_bill_statuses()
    {
        var vendorId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();
        var submittedBillId = Guid.NewGuid();
        var draftBillId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();

        var computation = BillReceiptMatchingPolicy.Compute(
            new[]
            {
                new BillReceiptMatchBillLineCandidate(
                    draftBillId,
                    "draft",
                    1,
                    new DateOnly(2026, 4, 18),
                    new DateTimeOffset(2026, 4, 18, 9, 0, 0, TimeSpan.Zero),
                    vendorId,
                    itemId,
                    warehouseId,
                    "EA",
                    4m),
                new BillReceiptMatchBillLineCandidate(
                    submittedBillId,
                    "submitted",
                    1,
                    new DateOnly(2026, 4, 19),
                    new DateTimeOffset(2026, 4, 19, 9, 0, 0, TimeSpan.Zero),
                    vendorId,
                    itemId,
                    warehouseId,
                    "EA",
                    4m)
            },
            new[]
            {
                new BillReceiptMatchReceiptLineCandidate(
                    receiptId,
                    1,
                    new DateOnly(2026, 4, 19),
                    new DateTimeOffset(2026, 4, 19, 8, 0, 0, TimeSpan.Zero),
                    vendorId,
                    itemId,
                    warehouseId,
                    "EA",
                    5m)
            });

        Assert.Equal(5m, computation.Allocations.Sum(static allocation => allocation.MatchedQuantity));
        Assert.Equal(4m, computation.Allocations.Where(allocation => allocation.BillId == submittedBillId).Sum(static allocation => allocation.MatchedQuantity));
        Assert.Equal(1m, computation.Allocations.Where(allocation => allocation.BillId == draftBillId).Sum(static allocation => allocation.MatchedQuantity));
        Assert.Equal("fully_covered", computation.LineStatuses[(submittedBillId, 1)].MatchStatus);
        Assert.Equal("partially_covered", computation.LineStatuses[(draftBillId, 1)].MatchStatus);
        Assert.Equal(3m, computation.LineStatuses[(draftBillId, 1)].RemainingQuantity);
    }
}
