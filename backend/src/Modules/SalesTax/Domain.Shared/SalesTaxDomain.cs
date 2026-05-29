// Sales Tax v2 Domain — shared records.
//
// See SALES_TAX_MODULE_DESIGN.md (repo root) for the architecture
// behind these types. This file holds value-object records consumed
// by the engine, fragment builder, snapshot persister, and reporting
// surfaces. Records are immutable; mutation goes through the
// Application layer.

namespace Citus.Modules.SalesTax.Domain.Shared;

/// <summary>
/// Cross-tenant jurisdiction catalog row. Identified by
/// (CountryCode, RegionCode, CityCode, RegimeType).
/// </summary>
public sealed record TaxJurisdiction(
    Guid Id,
    string CountryCode,
    string? RegionCode,
    string? CityCode,
    string DisplayName,
    string AuthorityName,
    string RegimeType,
    bool IsActive);

/// <summary>
/// Per-company registration in a single jurisdiction. Carries the
/// registration number and per-jurisdiction GL routing quintet.
/// </summary>
public sealed record TaxRegistration(
    Guid Id,
    string CompanyId,
    Guid JurisdictionId,
    string RegistrationNumber,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string FilingFrequency,
    string ReportingCalendar,
    string BaseCurrencyCode,
    Guid? CollectedClearingAccountId,
    Guid? RecoverableClearingAccountId,
    Guid? AdjustmentAccountId,
    Guid? ReturnLiabilityAccountId,
    Guid? ReturnReceivableAccountId,
    bool IsActive);

/// <summary>
/// User-facing selectable. Has 1..N <see cref="TaxCodeComponent"/>s.
/// </summary>
public sealed record TaxCodeV2(
    Guid Id,
    string CompanyId,
    string Code,
    string Name,
    string Treatment,
    string AppliesTo,
    bool IsActive,
    bool NeedsJurisdictionReview,
    Guid? LegacyTaxCodeId);

/// <summary>
/// The atomic piece of tax under a <see cref="TaxCodeV2"/>. Links to a
/// jurisdiction; carries recoverability + GL routing + compound flag.
/// </summary>
public sealed record TaxCodeComponent(
    Guid Id,
    string CompanyId,
    Guid TaxCodeId,
    Guid JurisdictionId,
    int Sequence,
    bool IsCompound,
    string RecoverabilityMode,
    decimal? RecoverablePercent,
    Guid? PayableAccountId,
    Guid? RecoverableAccountId,
    Guid? NonRecoverableAccountId,
    bool BoxMappingOverridden);

/// <summary>
/// Effective-dated rate. Look up by (ComponentId, AsOf) where AsOf
/// falls within [EffectiveFrom, EffectiveTo).
/// </summary>
public sealed record TaxCodeComponentRate(
    Guid Id,
    Guid ComponentId,
    decimal RatePercent,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo);

/// <summary>
/// Per-(document, line, sequence, leg) immutable snapshot row. The
/// source of truth for sales tax reporting and return preview.
/// </summary>
public sealed record DocumentLineTaxSnapshot(
    Guid Id,
    string CompanyId,
    string DocumentType,
    Guid DocumentId,
    Guid LineId,
    int Sequence,
    string Leg,
    Guid TaxCodeId,
    Guid ComponentId,
    Guid JurisdictionId,
    string CodeSnapshot,
    string NameSnapshot,
    string RegimeTypeSnapshot,
    string TreatmentSnapshot,
    decimal RatePercentSnapshot,
    bool IsCompoundSnapshot,
    IReadOnlyList<string> ReportingBoxCodes,
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal RecoverableAmount,
    decimal NonRecoverableAmount,
    string DocumentCurrencyCode,
    decimal TaxAmountBase,
    decimal FxRateSnapshot,
    DateTimeOffset ComputedAt);

/// <summary>
/// Tax-treatment values. String constants (not enum) to match the
/// schema's CHECK constraint and stay stable across version bumps.
/// </summary>
public static class TaxTreatment
{
    public const string Taxable = "taxable";
    public const string ZeroRated = "zero_rated";
    public const string Exempt = "exempt";
    public const string OutOfScope = "out_of_scope";
    public const string ReverseCharge = "reverse_charge";
    public const string ImportTax = "import_tax";
}

/// <summary>
/// Recoverability modes for the purchase side.
/// </summary>
public static class TaxRecoverabilityMode
{
    public const string Full = "full";
    public const string Partial = "partial";
    public const string None = "none";
}

/// <summary>
/// Snapshot leg discriminator. <c>Primary</c> covers ordinary single-
/// fragment lines; reverse-charge emits both self-assessed sides.
/// </summary>
public static class TaxSnapshotLeg
{
    public const string Primary = "primary";
    public const string SelfAssessedPayable = "self_assessed_payable";
    public const string SelfAssessedRecoverable = "self_assessed_recoverable";
}

/// <summary>
/// Document-side discriminator passed into the engine so it knows
/// whether to compute purchase-side recoverability splits.
/// </summary>
public enum SalesTaxDocumentSide
{
    Sales = 0,
    Purchase = 1,
}
