using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Companies;

namespace Citus.Accounting.Application.Invoices;

/// <summary>
/// Pure function — turns a raw invoice review row, the active company
/// profile, and an optional bill-to customer into the
/// <see cref="InvoiceRenderModel"/> shape the PDF / HTML preview both
/// consume. No IO. The endpoint owns the IO (review + customer +
/// company lookups) and hands the values to this builder.
/// </summary>
public static class InvoiceRenderModelBuilder
{
    public static InvoiceRenderModel Build(
        InvoiceReviewProjection review,
        CompanyProfileSnapshot company,
        CustomerRecord? customer,
        InvoiceTemplateConfig? template = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(company);

        var config = template ?? InvoiceTemplateConfig.Default;
        var branding = new InvoiceBrandingSummary(
            LogoUrl: config.LogoUrl,
            PrimaryColorHex: config.PrimaryColorHex,
            AccentColorHex: config.AccentColorHex,
            Tagline: config.Tagline,
            Greeting: config.Greeting,
            PaymentInstructions: config.PaymentInstructions,
            FooterNote: config.FooterNote,
            ShowTaxColumn: config.ShowTaxColumn);

        var issuer = new InvoiceIssuerSummary(
            CompanyName: company.LegalName,
            CompanyCode: company.EntityNumber,
            AddressBlock: ComposeAddress(
                company.AddressLine, company.City,
                company.ProvinceState, company.PostalCode,
                company.Country),
            Email: company.Email,
            Phone: company.Phone);

        var billTo = customer is null
            ? new InvoiceBillToSummary(
                DisplayName: review.CounterpartyDisplayName ?? "Customer",
                AddressBlock: null,
                Email: null,
                Phone: null)
            : new InvoiceBillToSummary(
                DisplayName: customer.DisplayName,
                AddressBlock: ComposeAddress(
                    customer.AddressLine, customer.City,
                    customer.ProvinceState, customer.PostalCode,
                    customer.Country),
                Email: customer.Email,
                Phone: customer.Phone);

        var header = new InvoiceHeaderSummary(
            DisplayNumber: review.DisplayNumber,
            EntityNumber: review.EntityNumber,
            DocumentDate: review.DocumentDate,
            DueDate: review.DueDate,
            Status: review.Status,
            Memo: review.Memo);

        var lines = review.Lines
            .Select(line => new InvoiceRenderLine(
                LineNumber: line.LineNumber,
                Description: line.Description ?? string.Empty,
                Quantity: line.Quantity,
                UnitPrice: line.UnitPrice,
                LineAmount: line.LineAmount,
                TaxAmount: line.TaxAmount))
            .ToArray();

        var totals = new InvoiceTotalsSummary(
            Subtotal: review.SubtotalAmount,
            Tax: review.TaxAmount,
            Total: review.TotalAmount,
            CurrencyCode: review.TransactionCurrencyCode);

        return new InvoiceRenderModel
        {
            Issuer = issuer,
            BillTo = billTo,
            Header = header,
            Lines = lines,
            Totals = totals,
            Branding = branding,
        };
    }

    private static string? ComposeAddress(
        string? line, string? city, string? region, string? postal, string? country)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(line)) parts.Add(line!.Trim());

        var line2 = string.Join(", ",
            new[] { city, region }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim()));

        var line3 = string.Join(" ",
            new[] { postal, country }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim()));

        if (!string.IsNullOrWhiteSpace(line2)) parts.Add(line2);
        if (!string.IsNullOrWhiteSpace(line3)) parts.Add(line3);

        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }
}

/// <summary>
/// Minimal projection of <c>IAccountingDocumentReview</c> the renderer
/// needs. Defined here so the Application project doesn't need a runtime
/// dependency on the existing review repository's record type.
/// </summary>
public sealed record InvoiceReviewProjection(
    string DisplayNumber,
    string EntityNumber,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string Status,
    string? CounterpartyDisplayName,
    string TransactionCurrencyCode,
    decimal SubtotalAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    IReadOnlyList<InvoiceReviewLineProjection> Lines);

public sealed record InvoiceReviewLineProjection(
    int LineNumber,
    string? Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal LineAmount,
    decimal TaxAmount);
