namespace Citus.Business.Blazor.Services;

public static class AccountingDocumentReviewRouteCatalog
{
    public static string GetSourceLabel(string? sourceType) =>
        Normalize(sourceType) switch
        {
            "manual_journal" => "Manual Journal",
            "invoice" => "Invoice",
            "credit_note" => "Credit Note",
            "bill" => "Bill",
            "vendor_credit" => "Vendor Credit",
            "receive_payment" => "Receive Payment",
            "credit_application" => "Credit Application",
            "pay_bill" => "Pay Bill",
            "vendor_credit_application" => "Vendor Credit Application",
            _ => "Document"
        };

    public static bool IsSupported(string? sourceType) => Normalize(sourceType) is not null;

    public static bool TryBuildApiPath(string? sourceType, Guid documentId, out string requestPath)
    {
        var normalized = Normalize(sourceType);
        requestPath = normalized is null
            ? string.Empty
            : $"accounting/document-review/{normalized}/{documentId:D}";

        return normalized is not null;
    }

    public static bool TryBuildReviewHref(string? sourceType, Guid documentId, out string href)
    {
        var normalized = Normalize(sourceType);
        href = normalized is null
            ? string.Empty
            : $"documents/{normalized}/{documentId:D}";

        return normalized is not null;
    }

    private static string? Normalize(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return null;
        }

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "manual_journal" => "manual_journal",
            "invoice" => "invoice",
            "credit_note" => "credit_note",
            "bill" => "bill",
            "vendor_credit" => "vendor_credit",
            "receive_payment" => "receive_payment",
            "credit_application" => "credit_application",
            "pay_bill" => "pay_bill",
            "vendor_credit_application" => "vendor_credit_application",
            _ => null
        };
    }
}
