using Citus.Business.Blazor.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class AccountingDocumentReviewRouteCatalogTests
{
    [Theory]
    [InlineData("manual_journal", "accounting/document-review/manual_journal/{0}", "documents/manual_journal/{0}", "Manual Journal")]
    [InlineData("invoice", "accounting/document-review/invoice/{0}", "documents/invoice/{0}", "Invoice")]
    [InlineData("credit_note", "accounting/document-review/credit_note/{0}", "documents/credit_note/{0}", "Credit Note")]
    [InlineData("bill", "accounting/document-review/bill/{0}", "documents/bill/{0}", "Bill")]
    [InlineData("vendor_credit", "accounting/document-review/vendor_credit/{0}", "documents/vendor_credit/{0}", "Vendor Credit")]
    public void SupportedSourceTypes_BuildApiPathAndHref(
        string sourceType,
        string apiPattern,
        string hrefPattern,
        string expectedLabel)
    {
        var documentId = Guid.Parse("d6a6a7a2-66cd-4ac7-90ab-c9d7fb8439fe");

        Assert.True(AccountingDocumentReviewRouteCatalog.TryBuildApiPath(sourceType, documentId, out var apiPath));
        Assert.True(AccountingDocumentReviewRouteCatalog.TryBuildReviewHref(sourceType, documentId, out var href));
        Assert.Equal(string.Format(apiPattern, documentId.ToString("D")), apiPath);
        Assert.Equal(string.Format(hrefPattern, documentId.ToString("D")), href);
        Assert.Equal(expectedLabel, AccountingDocumentReviewRouteCatalog.GetSourceLabel(sourceType));
    }

    [Fact]
    public void UnsupportedSourceType_ReturnsFalseAndDocumentLabel()
    {
        var documentId = Guid.NewGuid();

        Assert.False(AccountingDocumentReviewRouteCatalog.TryBuildApiPath("receive_payment", documentId, out var apiPath));
        Assert.False(AccountingDocumentReviewRouteCatalog.TryBuildReviewHref("receive_payment", documentId, out var href));
        Assert.Equal(string.Empty, apiPath);
        Assert.Equal(string.Empty, href);
        Assert.Equal("Document", AccountingDocumentReviewRouteCatalog.GetSourceLabel("receive_payment"));
        Assert.False(AccountingDocumentReviewRouteCatalog.IsSupported("receive_payment"));
    }
}
