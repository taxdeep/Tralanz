namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Sales Tax redesign (R2): per-company catalog of "Tax Codes" — the
/// user-defined bundles in <c>tax_code_sets</c>, each grouping one or more
/// Tax Rules (the existing <c>tax_codes</c> rows) via
/// <c>tax_code_set_rules</c>. Backs the document line tax pickers (list) and
/// the Tax Code editor (create / edit / activate).
/// </summary>
public sealed record TaxCodeSetRecord(
    Guid Id,
    string Code,
    string Name,
    string AppliesTo,
    bool IsActive,
    IReadOnlyList<TaxCodeSetMemberRecord> Members);

/// <summary>One member Rule of a Tax Code, in JE-leg order.</summary>
public sealed record TaxCodeSetMemberRecord(
    Guid RuleId,
    string RuleCode,
    string RuleName,
    decimal RatePercent,
    int Sequence,
    bool IsCompound);

public sealed record TaxCodeSetUpsertInput(
    string Code,
    string Name,
    string AppliesTo,
    bool IsActive,
    IReadOnlyList<TaxCodeSetMemberInput> Members);

public sealed record TaxCodeSetMemberInput(
    Guid RuleId,
    int Sequence,
    bool IsCompound);

public interface ITaxCodeSetStore
{
    Task<IReadOnlyList<TaxCodeSetRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<TaxCodeSetRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid taxCodeSetId,
        CancellationToken cancellationToken);

    Task<TaxCodeSetRecord> CreateAsync(
        CompanyId companyId,
        TaxCodeSetUpsertInput input,
        CancellationToken cancellationToken);

    Task<TaxCodeSetRecord?> UpdateAsync(
        CompanyId companyId,
        Guid taxCodeSetId,
        TaxCodeSetUpsertInput input,
        CancellationToken cancellationToken);

    Task<TaxCodeSetRecord?> SetActiveAsync(
        CompanyId companyId,
        Guid taxCodeSetId,
        bool isActive,
        CancellationToken cancellationToken);
}
