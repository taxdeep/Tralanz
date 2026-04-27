namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-company tax code catalog. Backs the Settings → Tax Rates surface
/// and the per-line Sales Tax pickers on document forms (invoice, bill,
/// journal entry).
///
/// V1 surfaces only the fields the UI needs today: identity, code, name,
/// rate, applies_to scope, active flag. The full migration-draft schema
/// (recoverability_mode, is_recoverable_on_purchase, payable_account_id,
/// recoverable_account_id, entity_number) is preserved server-side with
/// safe defaults so the existing Posting Engine fragment builder stays
/// happy. Recoverability + account routing become user-editable in a
/// later batch.
/// </summary>
public sealed record TaxCodeRecord(
    Guid Id,
    Guid CompanyId,
    string EntityNumber,
    string Code,
    string Name,
    decimal RatePercent,
    string AppliesTo,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class TaxCodeAppliesTo
{
    public const string Sales = "sales";
    public const string Purchase = "purchase";
    public const string Both = "both";

    public static bool IsValid(string? value) => value is Sales or Purchase or Both;
}

public sealed record TaxCodeUpsertInput(
    string Code,
    string Name,
    decimal RatePercent,
    string AppliesTo,
    bool IsActive);

public interface ITaxCodeStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxCodeRecord>> ListAsync(
        Guid companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<TaxCodeRecord?> GetByIdAsync(
        Guid companyId,
        Guid taxCodeId,
        CancellationToken cancellationToken);

    Task<TaxCodeRecord> CreateAsync(
        Guid companyId,
        TaxCodeUpsertInput input,
        CancellationToken cancellationToken);

    Task<TaxCodeRecord?> UpdateAsync(
        Guid companyId,
        Guid taxCodeId,
        TaxCodeUpsertInput input,
        CancellationToken cancellationToken);

    Task<TaxCodeRecord?> SetActiveAsync(
        Guid companyId,
        Guid taxCodeId,
        bool isActive,
        CancellationToken cancellationToken);
}
