using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Domain.Tests;

public sealed class PurchaseOrderDocumentStatusesTests
{
    [Fact]
    public void CanEdit_AllowsDraftOnly()
    {
        Assert.True(PurchaseOrderDocumentStatuses.CanEdit("draft"));
        Assert.False(PurchaseOrderDocumentStatuses.CanEdit("approved"));
        Assert.False(PurchaseOrderDocumentStatuses.CanEdit("issued"));
        Assert.False(PurchaseOrderDocumentStatuses.CanEdit("closed"));
        Assert.False(PurchaseOrderDocumentStatuses.CanEdit("cancelled"));
    }

    [Fact]
    public void CanIssue_AllowsApprovedOnly()
    {
        Assert.False(PurchaseOrderDocumentStatuses.CanIssue("draft"));
        Assert.True(PurchaseOrderDocumentStatuses.CanIssue("approved"));
        Assert.False(PurchaseOrderDocumentStatuses.CanIssue("issued"));
    }

    [Fact]
    public void CanReopenForAmendment_AllowsApprovedAndIssuedOnly()
    {
        Assert.False(PurchaseOrderDocumentStatuses.CanReopenForAmendment("draft"));
        Assert.True(PurchaseOrderDocumentStatuses.CanReopenForAmendment("approved"));
        Assert.True(PurchaseOrderDocumentStatuses.CanReopenForAmendment("issued"));
        Assert.False(PurchaseOrderDocumentStatuses.CanReopenForAmendment("closed"));
        Assert.False(PurchaseOrderDocumentStatuses.CanReopenForAmendment("cancelled"));
    }

    [Fact]
    public void Normalize_RejectsUnknownStatus()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PurchaseOrderDocumentStatuses.Normalize("posted"));

        Assert.Contains("draft, approved, issued, closed, or cancelled", error.Message, StringComparison.Ordinal);
    }
}
