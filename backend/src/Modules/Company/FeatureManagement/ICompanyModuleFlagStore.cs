namespace Modules.Company.FeatureManagement;

public interface ICompanyModuleFlagStore
{
    /// <summary>Idempotent DDL bootstrap; safe to call repeatedly.</summary>
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns every catalog entry for the given company, joined with
    /// the persisted state. Missing rows are returned with
    /// Enabled=false / UpdatedAtUtc=null so the caller always sees the
    /// full catalog in one shot.
    /// </summary>
    Task<IReadOnlyList<CompanyModuleFlagSummary>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cheap point-lookup used by the API gate. Returns false when no
    /// row exists OR the row's enabled column is false.
    /// </summary>
    Task<bool> IsEnabledAsync(
        CompanyId companyId,
        string moduleKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Upserts the toggle state and writes a single audit_logs row in
    /// the same transaction. Returns the post-write summary plus
    /// whether the value actually changed (no-op toggles still write
    /// an audit row when <paramref name="forceAuditOnNoChange"/> is
    /// true; otherwise they're silent).
    /// </summary>
    Task<CompanyModuleFlagUpdateResult> SetEnabledAsync(
        CompanyId companyId,
        string moduleKey,
        bool enabled,
        string reason,
        string actorType,
        UserId? actorUserId,
        bool forceAuditOnNoChange,
        CancellationToken cancellationToken);
}
