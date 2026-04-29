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
    /// Free-text payment instructions / footer note. Empty in v1 (template
    /// editor lands in batch 3). Caller may pre-populate from
    /// company-level config later.
    /// </summary>
    public string PaymentInstructions { get; init; } = string.Empty;
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
