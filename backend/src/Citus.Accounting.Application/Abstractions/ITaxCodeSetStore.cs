namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Sales Tax redesign (R2): per-company catalog of "Tax Codes" — the
/// user-defined bundles in <c>tax_code_sets</c>, each grouping one or more
/// Tax Rules (the existing <c>tax_codes</c> rows) via
/// <c>tax_code_set_rules</c>. Backs the document line tax pickers and the
/// Tax Code editor. Read-only for slice 1a (the picker); create/edit lands
/// with the editor (slice 2).
/// </summary>
public sealed record TaxCodeSetRecord(
    Guid Id,
    string Code,
    string Name,
    string AppliesTo,
    bool IsActive,
    int RuleCount);

public interface ITaxCodeSetStore
{
    Task<IReadOnlyList<TaxCodeSetRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken);
}
