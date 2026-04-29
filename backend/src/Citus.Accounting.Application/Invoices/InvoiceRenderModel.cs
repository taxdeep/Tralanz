namespace Citus.Accounting.Application.Invoices;

/// <summary>
/// Pure data shape that drives invoice rendering — both the PDF
/// renderer and the future HTML preview consume this same model so a
/// downloaded PDF and the on-screen "live preview" stay byte-for-byte
/// in agreement on what fields are shown and how they read.
///
/// Batch 1 of the invoice send / template work uses a single hard-coded
/// "default" template. Future batches will swap fields here for values
/// loaded from <c>invoice_templates</c>.
/// </summary>
public sealed record InvoiceRenderModel
{
    public required InvoiceIssuerSummary Issuer { get; init; }

    public required InvoiceBillToSummary BillTo { get; init; }

    public required InvoiceHeaderSummary Header { get; init; }

    public IReadOnlyList<InvoiceRenderLine> Lines { get; init; } = Array.Empty<InvoiceRenderLine>();

    public required InvoiceTotalsSummary Totals { get; init; }

    /// <summary>
    /// Branding + customizable text blocks. Sourced from the company's
    /// default <see cref="InvoiceTemplate"/> in batch 3+; falls back to
    /// <see cref="InvoiceBrandingSummary.Default"/> when no template is
    /// available so existing call sites keep working.
    /// </summary>
    public InvoiceBrandingSummary Branding { get; init; } = InvoiceBrandingSummary.Default;

    /// <summary>
    /// Convenience pass-through to <see cref="InvoiceBrandingSummary.PaymentInstructions"/>
    /// for renderers that don't care about the rest of the branding
    /// surface. Kept for source compatibility with batch 1 / 2 call sites.
    /// </summary>
    public string PaymentInstructions => Branding.PaymentInstructions;
}

public sealed record InvoiceBrandingSummary(
    string? LogoUrl,
    string PrimaryColorHex,
    string AccentColorHex,
    string? Tagline,
    string Greeting,
    string PaymentInstructions,
    string FooterNote,
    bool ShowTaxColumn)
{
    public static InvoiceBrandingSummary Default => new(
        LogoUrl: null,
        PrimaryColorHex: "#1f2937",
        AccentColorHex: "#6b7280",
        Tagline: null,
        Greeting: "Thank you for your business.",
        PaymentInstructions: string.Empty,
        FooterNote: "Thank you for your business.",
        ShowTaxColumn: true);
}

public sealed record InvoiceIssuerSummary(
    string CompanyName,
    string CompanyCode,
    string? AddressBlock,
    string? Email,
    string? Phone);

public sealed record InvoiceBillToSummary(
    string DisplayName,
    string? AddressBlock,
    string? Email,
    string? Phone);

public sealed record InvoiceHeaderSummary(
    string DisplayNumber,
    string EntityNumber,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string Status,
    string? Memo);

public sealed record InvoiceRenderLine(
    int LineNumber,
    string Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal LineAmount,
    decimal TaxAmount);

public sealed record InvoiceTotalsSummary(
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    string CurrencyCode);
