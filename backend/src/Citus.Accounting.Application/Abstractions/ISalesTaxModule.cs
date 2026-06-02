using SharedKernel.Identity;

namespace Citus.Accounting.Application.Abstractions;

public static class SalesTaxDocumentSide
{
    public const string Sales = "sales";
    public const string Purchase = "purchase";

    public static bool IsValid(string? value) => value is Sales or Purchase;
}

public static class SalesTaxTreatment
{
    public const string Taxable = "taxable";
    public const string ZeroRated = "zero_rated";
    public const string Exempt = "exempt";
    public const string OutOfScope = "out_of_scope";
    public const string ReverseCharge = "reverse_charge";
    public const string ImportTax = "import_tax";
}

public static class SalesTaxRecoverability
{
    public const string Recoverable = "recoverable";
    public const string PartiallyRecoverable = "partially_recoverable";
    public const string NonRecoverable = "non_recoverable";
    public const string NotApplicable = "not_applicable";
}

public sealed record SalesTaxJurisdictionRecord(
    Guid Id,
    CompanyId CompanyId,
    string CountryCode,
    string? RegionCode,
    string? LocalityName,
    string Level,
    Guid? ParentId,
    bool IsActive);

public sealed record SalesTaxAuthorityRecord(
    Guid Id,
    CompanyId CompanyId,
    Guid? JurisdictionId,
    string Code,
    string Name,
    string AuthorityType,
    bool IsActive);

public sealed record SalesTaxRegistrationRecord(
    Guid Id,
    CompanyId CompanyId,
    Guid AuthorityId,
    string RegistrationNumber,
    string FilingFrequency,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive);

public sealed record SalesTaxComponentRecord(
    Guid Id,
    CompanyId CompanyId,
    Guid? AuthorityId,
    string Code,
    string Name,
    string TaxType,
    string AppliesTo,
    string Treatment,
    string Recoverability,
    decimal CurrentRatePercent,
    Guid? PayableAccountId,
    Guid? RecoverableAccountId,
    string? RegistrationNumber,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive);

public sealed record SalesTaxRuleUpsertInput(
    string Code,
    string Name,
    string TaxType,
    string AppliesTo,
    string Treatment,
    string Recoverability,
    decimal RatePercent,
    Guid? PayableAccountId,
    Guid? RecoverableAccountId,
    string? RegistrationNumber,
    bool IsActive);

public sealed record SalesTaxCodeComponentRecord(
    Guid TaxCodeId,
    Guid? TaxComponentId,
    string Code,
    string Name,
    string TaxType,
    string AppliesTo,
    decimal RatePercent,
    int Sequence,
    string CompoundMode,
    string Treatment,
    string Recoverability,
    decimal RecoverablePercent,
    string? RegistrationNumber);

public sealed record SalesTaxCodeUpsertInput(
    string Code,
    string Name,
    string AppliesTo,
    string? RegistrationNumber,
    bool IsActive,
    IReadOnlyList<SalesTaxCodeComponentUpsertInput> Components);

public sealed record SalesTaxCodeComponentUpsertInput(
    decimal RatePercent,
    string TaxType,
    string Recoverability,
    string AppliesTo,
    string? RegistrationNumber,
    Guid? TaxRuleId = null);

public sealed record SalesTaxCodeRecord(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string Code,
    string Name,
    string Treatment,
    string AppliesTo,
    decimal SalesRatePercent,
    decimal PurchaseRatePercent,
    string? RegistrationNumber,
    bool IsGroup,
    bool IsActive,
    IReadOnlyList<SalesTaxCodeComponentRecord> Components,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SalesTaxPreviewRequest(
    string DocumentSide,
    DateOnly DocumentDate,
    string TaxMode,
    decimal Amount,
    Guid? TaxCodeId,
    string CurrencyCode);

public sealed record SalesTaxPreviewLine(
    Guid? TaxCodeId,
    Guid? TaxComponentId,
    string Code,
    string Name,
    decimal TaxableAmount,
    decimal RatePercent,
    decimal TaxAmount,
    decimal RecoverableAmount,
    decimal NonRecoverableAmount,
    string Treatment,
    string Recoverability,
    string ReportingCategory);

public sealed record SalesTaxPreviewResult(
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal RecoverableAmount,
    decimal NonRecoverableAmount,
    decimal GrossAmount,
    string CurrencyCode,
    IReadOnlyList<SalesTaxPreviewLine> Lines);

public sealed record SalesTaxReportSummaryRow(
    string JurisdictionCode,
    string RegistrationNumber,
    string TaxComponentCode,
    string ReportingCategory,
    decimal TaxableAmount,
    decimal TaxCollected,
    decimal InputTaxRecoverable,
    decimal NonRecoverableTax,
    decimal NetTax);

public sealed record SalesTaxReportDetailRow(
    DateOnly DocumentDate,
    string DocumentType,
    Guid DocumentId,
    Guid? DocumentLineId,
    string TaxCode,
    string TaxComponentCode,
    string JurisdictionCode,
    decimal TaxableAmount,
    decimal RatePercent,
    decimal TaxAmount,
    decimal RecoverableAmount,
    decimal NonRecoverableAmount,
    string ReportingCategory);

public interface ISalesTaxStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesTaxComponentRecord>> ListTaxRulesAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<SalesTaxComponentRecord> CreateTaxRuleAsync(
        CompanyId companyId,
        SalesTaxRuleUpsertInput input,
        CancellationToken cancellationToken);

    Task<SalesTaxComponentRecord?> UpdateTaxRuleAsync(
        CompanyId companyId,
        Guid taxRuleId,
        SalesTaxRuleUpsertInput input,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesTaxCodeRecord>> ListTaxCodesAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<SalesTaxCodeRecord> CreateTaxCodeAsync(
        CompanyId companyId,
        SalesTaxCodeUpsertInput input,
        CancellationToken cancellationToken);

    Task<SalesTaxCodeRecord?> UpdateTaxCodeAsync(
        CompanyId companyId,
        Guid taxCodeId,
        SalesTaxCodeUpsertInput input,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesTaxReportSummaryRow>> GetSummaryReportAsync(
        CompanyId companyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesTaxReportDetailRow>> GetDetailReportAsync(
        CompanyId companyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken);
}

public interface ISalesTaxCalculationEngine
{
    Task<SalesTaxPreviewResult> CalculateAsync(
        CompanyId companyId,
        SalesTaxPreviewRequest request,
        CancellationToken cancellationToken);
}
