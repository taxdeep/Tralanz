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
    public void Receipt_line_requires_item_quantity_and_uom()
    {
        Assert.Throws<ArgumentException>(() => new ReceiptDocumentLine(1, Guid.Empty, 1m, "EA"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReceiptDocumentLine(1, Guid.NewGuid(), 0m, "EA"));
        Assert.Throws<ArgumentException>(() => new ReceiptDocumentLine(1, Guid.NewGuid(), 1m, ""));
    }

    [Fact]
    public void Receipt_document_requires_vendor_warehouse_and_lines()
    {
        var companyId = new CompanyId(Guid.NewGuid());
        var entityNumber = new EntityNumber("EN202600000001");
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
