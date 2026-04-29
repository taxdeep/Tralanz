namespace Citus.Accounting.Application.Invoices;

/// <summary>
/// Per-company invoice templates. Each company gets a small set of named
/// branding presets seeded on first read (Modern / Classic / Minimal).
/// One template is always marked default — that's the one the PDF
/// renderer and email composer use when an invoice is downloaded or
/// emailed without an explicit template override.
///
/// Schema is intentionally a single jsonb column (config) so adding a
/// new branding knob does not need a migration. Top-level columns stay
/// queryable: id / company_id / name / is_default / timestamps.
/// </summary>
public interface IInvoiceTemplateStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<InvoiceTemplate>> ListByCompanyAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InvoiceTemplate?> GetByIdAsync(
        Guid companyId,
        Guid templateId,
        CancellationToken cancellationToken);

    Task<InvoiceTemplate?> GetDefaultAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<InvoiceTemplate> CreateAsync(
        Guid companyId,
        InvoiceTemplateUpsertRequest request,
        CancellationToken cancellationToken);

    Task<InvoiceTemplate?> UpdateAsync(
        Guid companyId,
        Guid templateId,
        InvoiceTemplateUpsertRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the given template as the company's default and clears
    /// is_default on every other template in the same company in one
    /// transaction. Returns the freshly-default template, or null if the
    /// id wasn't found in the company.
    /// </summary>
    Task<InvoiceTemplate?> SetDefaultAsync(
        Guid companyId,
        Guid templateId,
        CancellationToken cancellationToken);
}

public sealed record InvoiceTemplate(
    Guid Id,
    Guid CompanyId,
    string Name,
    bool IsDefault,
    InvoiceTemplateConfig Config,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InvoiceTemplateUpsertRequest(
    string Name,
    InvoiceTemplateConfig Config);

/// <summary>
/// All operator-customizable branding knobs in one place. Adding a new
/// knob means adding a property here, defaulting it sensibly, and
/// reading it from the renderer / composer — no schema change.
/// </summary>
public sealed record InvoiceTemplateConfig(
    string? LogoUrl,
    string PrimaryColorHex,
    string AccentColorHex,
    string? Tagline,
    string Greeting,
    string PaymentInstructions,
    string FooterNote,
    bool ShowTaxColumn,
    string EmailSubjectTemplate,
    string EmailBodyTemplate)
{
    public static InvoiceTemplateConfig Default => new(
        LogoUrl: null,
        PrimaryColorHex: "#1f2937",
        AccentColorHex: "#6b7280",
        Tagline: null,
        Greeting: "Thank you for your business.",
        PaymentInstructions: string.Empty,
        FooterNote: "Thank you for your business.",
        ShowTaxColumn: true,
        EmailSubjectTemplate: "Invoice {invoiceNumber} from {companyName}",
        EmailBodyTemplate: "");
}
