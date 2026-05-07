using Citus.Accounting.Application;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Api.Tests;

public sealed class ReceiptDraftApiTests
{
    [Fact]
    public void Receipt_status_helpers_only_allow_draft_and_posted()
    {
        Assert.Equal(ReceiptDocumentStatuses.Draft, ReceiptDocumentStatuses.Normalize("draft"));
        Assert.Equal(ReceiptDocumentStatuses.Posted, ReceiptDocumentStatuses.Normalize("posted"));
        Assert.True(ReceiptDocumentStatuses.CanEdit("draft"));
        Assert.False(ReceiptDocumentStatuses.CanEdit("posted"));
        Assert.True(ReceiptDocumentStatuses.CanPost("draft"));
        Assert.False(ReceiptDocumentStatuses.CanPost("posted"));
        Assert.Throws<InvalidOperationException>(() => ReceiptDocumentStatuses.Normalize("submitted"));
    }

    [Fact]
    public void Purchase_order_status_helpers_are_foundation_only()
    {
        Assert.Equal(PurchaseOrderDocumentStatuses.Draft, PurchaseOrderDocumentStatuses.Normalize("draft"));
        Assert.Equal(PurchaseOrderDocumentStatuses.Approved, PurchaseOrderDocumentStatuses.Normalize("approved"));
        Assert.Equal(PurchaseOrderDocumentStatuses.Issued, PurchaseOrderDocumentStatuses.Normalize("issued"));
        Assert.Equal(PurchaseOrderDocumentStatuses.Closed, PurchaseOrderDocumentStatuses.Normalize("closed"));
        Assert.Equal(PurchaseOrderDocumentStatuses.Cancelled, PurchaseOrderDocumentStatuses.Normalize("cancelled"));
        Assert.True(PurchaseOrderDocumentStatuses.CanEdit("draft"));
        Assert.True(PurchaseOrderDocumentStatuses.CanApprove("draft"));
        Assert.False(PurchaseOrderDocumentStatuses.CanApprove("approved"));
        Assert.False(PurchaseOrderDocumentStatuses.CanIssue("draft"));
        Assert.True(PurchaseOrderDocumentStatuses.CanIssue("approved"));
        Assert.False(PurchaseOrderDocumentStatuses.CanEdit("issued"));
        Assert.False(PurchaseOrderDocumentStatuses.CanIssue("closed"));
        Assert.False(PurchaseOrderDocumentStatuses.CanReopenForAmendment("draft"));
        Assert.True(PurchaseOrderDocumentStatuses.CanReopenForAmendment("approved"));
        Assert.True(PurchaseOrderDocumentStatuses.CanReopenForAmendment("issued"));
        Assert.False(PurchaseOrderDocumentStatuses.CanReopenForAmendment("closed"));
        Assert.True(PurchaseOrderDocumentStatuses.CanClose("issued"));
        Assert.False(PurchaseOrderDocumentStatuses.CanClose("draft"));
        Assert.True(PurchaseOrderDocumentStatuses.CanCancel("draft"));
        Assert.True(PurchaseOrderDocumentStatuses.CanCancel("approved"));
        Assert.True(PurchaseOrderDocumentStatuses.CanCancel("issued"));
        Assert.False(PurchaseOrderDocumentStatuses.CanCancel("closed"));
        Assert.Throws<InvalidOperationException>(() => PurchaseOrderDocumentStatuses.Normalize("posted"));
    }

    [Theory]
    [InlineData(PurchaseOrderDocumentStatuses.Draft, false)]
    [InlineData(PurchaseOrderDocumentStatuses.Approved, false)]
    [InlineData(PurchaseOrderDocumentStatuses.Issued, true)]
    [InlineData(PurchaseOrderDocumentStatuses.Closed, false)]
    [InlineData(PurchaseOrderDocumentStatuses.Cancelled, false)]
    public void Purchase_order_anchor_policy_only_allows_issued_purchase_orders(string status, bool expected)
    {
        Assert.Equal(expected, PurchaseOrderAnchorPolicy.AllowsNewAnchor(status));
        if (expected)
        {
            PurchaseOrderAnchorPolicy.EnsureAllowsNewAnchor(status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => PurchaseOrderAnchorPolicy.EnsureAllowsNewAnchor(status));
        }
    }

    [Fact]
    public void Receipt_line_requires_item_quantity_and_uom()
    {
        Assert.Throws<ArgumentException>(() => new ReceiptDocumentLine(1, Guid.Empty, 1m, "EA"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReceiptDocumentLine(1, Guid.NewGuid(), 0m, "EA"));
        Assert.Throws<ArgumentException>(() => new ReceiptDocumentLine(1, Guid.NewGuid(), 1m, ""));
        Assert.Throws<InvalidOperationException>(() => new ReceiptDocumentLine(1, Guid.NewGuid(), 1m, "EA", purchaseOrderId: Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => new ReceiptDocumentLine(1, Guid.NewGuid(), 1m, "EA", purchaseOrderLineNumber: 1));
    }

    [Fact]
    public void Purchase_order_document_requires_ordered_truth()
    {
        Assert.Throws<ArgumentException>(() => new PurchaseOrderDocumentLine(1, Guid.Empty, 1m, "EA"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PurchaseOrderDocumentLine(1, Guid.NewGuid(), 0m, "EA"));
        Assert.Throws<ArgumentException>(() => new PurchaseOrderDocumentLine(1, Guid.NewGuid(), 1m, ""));

        var line = new PurchaseOrderDocumentLine(1, Guid.NewGuid(), 2.5m, "kg");
        Assert.Equal("KG", line.UomCode);

        Assert.Throws<InvalidOperationException>(() => new PurchaseOrderDocument(
            Guid.NewGuid(),
            CompanyId.FromOrdinal(1),
            EntityNumber.Create(2026, 1),
            new DocumentNumber("PO-000001"),
            PurchaseOrderDocumentStatuses.Draft,
            Guid.NewGuid(),
            new DateOnly(2026, 4, 20),
            Array.Empty<PurchaseOrderDocumentLine>()));
    }

    [Theory]
    [InlineData(10, 0, 0, PurchaseOrderThreeQuantityStatusPolicy.OrderedOnly)]
    [InlineData(10, 5, 0, PurchaseOrderThreeQuantityStatusPolicy.PartiallyReceived)]
    [InlineData(10, 10, 0, PurchaseOrderThreeQuantityStatusPolicy.FullyReceived)]
    [InlineData(10, 10, 5, PurchaseOrderThreeQuantityStatusPolicy.PartiallyBilled)]
    [InlineData(10, 10, 10, PurchaseOrderThreeQuantityStatusPolicy.FullyBilled)]
    [InlineData(10, 11, 0, PurchaseOrderThreeQuantityStatusPolicy.OverReceived)]
    [InlineData(10, 10, 11, PurchaseOrderThreeQuantityStatusPolicy.OverBilled)]
    [InlineData(10, 4, 5, PurchaseOrderThreeQuantityStatusPolicy.BilledAheadOfReceived)]
    public void Purchase_order_three_quantity_status_policy_exposes_control_truth(
        decimal orderedQuantity,
        decimal receivedQuantity,
        decimal billedQuantity,
        string expected)
    {
        Assert.Equal(expected, PurchaseOrderThreeQuantityStatusPolicy.ResolveLineStatus(
            orderedQuantity,
            receivedQuantity,
            billedQuantity));
    }

    [Fact]
    public void Purchase_order_quantity_discrepancy_policy_maps_open_control_states()
    {
        var overReceipt = new PurchaseOrderLineThreeQuantitySummary(
            1,
            Guid.NewGuid(),
            "EA",
            10m,
            11m,
            0m,
            0m,
            10m,
            PurchaseOrderThreeQuantityStatusPolicy.OverReceived);
        var billedAhead = overReceipt with
        {
            ReceivedQuantity = 4m,
            BilledQuantity = 5m,
            QuantityStatus = PurchaseOrderThreeQuantityStatusPolicy.BilledAheadOfReceived
        };

        Assert.Equal(PurchaseOrderQuantityDiscrepancyPolicy.OverReceived, PurchaseOrderQuantityDiscrepancyPolicy.ResolveDiscrepancyType(overReceipt));
        Assert.Equal(PurchaseOrderQuantityDiscrepancyPolicy.BilledAheadOfReceived, PurchaseOrderQuantityDiscrepancyPolicy.ResolveDiscrepancyType(billedAhead));
        Assert.True(PurchaseOrderQuantityDiscrepancyPolicy.IsDiscrepancyStatus(PurchaseOrderThreeQuantityStatusPolicy.OverBilled));
        Assert.False(PurchaseOrderQuantityDiscrepancyPolicy.IsDiscrepancyStatus(PurchaseOrderThreeQuantityStatusPolicy.FullyBilled));
        Assert.Equal(PurchaseOrderQuantityDiscrepancyPolicy.Open, PurchaseOrderQuantityDiscrepancyPolicy.NormalizeInvestigationStatus(null));
        Assert.Equal(PurchaseOrderQuantityDiscrepancyPolicy.Resolved, PurchaseOrderQuantityDiscrepancyPolicy.NormalizeInvestigationStatus(" resolved "));
        Assert.Equal(PurchaseOrderQuantityDiscrepancyPolicy.OverrideAuthorized, PurchaseOrderQuantityDiscrepancyPolicy.NormalizeInvestigationStatus("OVERRIDE_AUTHORIZED"));
        Assert.Equal(PurchaseOrderQuantityDiscrepancyPolicy.OverReceived, PurchaseOrderQuantityDiscrepancyPolicy.NormalizeDiscrepancyType(" over_received "));
        Assert.Equal(PurchaseOrderQuantityDiscrepancyPolicy.BilledAheadOfReceived, PurchaseOrderQuantityDiscrepancyPolicy.NormalizeDiscrepancyType("BILLED_AHEAD_OF_RECEIVED"));
        Assert.Throws<InvalidOperationException>(() => PurchaseOrderQuantityDiscrepancyPolicy.NormalizeDiscrepancyType("manual_override"));
        Assert.True(PurchaseOrderQuantityDiscrepancyPolicy.IsReviewVisibleStatus(PurchaseOrderQuantityDiscrepancyPolicy.OverrideAuthorized));
        Assert.False(PurchaseOrderQuantityDiscrepancyPolicy.IsReviewVisibleStatus(PurchaseOrderQuantityDiscrepancyPolicy.Resolved));
    }

    [Fact]
    public void Receipt_document_requires_vendor_warehouse_and_lines()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var entityNumber = EntityNumber.Create(2026, 1);
        var displayNumber = new DocumentNumber("RECEIPT-000001");
        var line = new ReceiptDocumentLine(1, Guid.NewGuid(), 2.5m, "kg");

        Assert.Throws<ArgumentException>(() => new ReceiptDocument(
            Guid.NewGuid(),
            companyId,
            entityNumber,
            displayNumber,
            ReceiptDocumentStatuses.Draft,
            Guid.Empty,
            Guid.NewGuid(),
            new DateOnly(2026, 4, 19),
            new[] { line }));

        Assert.Throws<ArgumentException>(() => new ReceiptDocument(
            Guid.NewGuid(),
            companyId,
            entityNumber,
            displayNumber,
            ReceiptDocumentStatuses.Draft,
            Guid.NewGuid(),
            Guid.Empty,
            new DateOnly(2026, 4, 19),
            new[] { line }));

        Assert.Throws<InvalidOperationException>(() => new ReceiptDocument(
            Guid.NewGuid(),
            companyId,
            entityNumber,
            displayNumber,
            ReceiptDocumentStatuses.Draft,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 4, 19),
            Array.Empty<ReceiptDocumentLine>()));
    }
}
