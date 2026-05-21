namespace Modules.Company.FeatureManagement;

public interface ICompanyModuleFlagWorkflow
{
    /// <summary>Catalog labels for the SysAdmin picker. Static.</summary>
    IReadOnlyList<CompanyModuleFlagOption> GetAvailableModules();

    Task<IReadOnlyList<CompanyModuleFlagSummary>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cached IsEnabled for API gating. Lookups inside the cache TTL
    /// don't touch the DB; toggle writes invalidate eagerly.
    /// </summary>
    Task<bool> IsEnabledAsync(
        CompanyId companyId,
        string moduleKey,
        CancellationToken cancellationToken);

    Task<CompanyModuleFlagUpdateResult> SetEnabledFromSysAdminAsync(
        CompanyId companyId,
        string moduleKey,
        bool enabled,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Business-side self-service toggle for the active company's Owner
    /// (or anyone the Owner has granted settings.modules.toggle to).
    /// Same persistence + cache-invalidate path as the SysAdmin
    /// pathway, but the audit row records actor_type='user' so post-hoc
    /// governance review can tell business-driven activations apart
    /// from platform-driven ones.
    /// </summary>
    Task<CompanyModuleFlagUpdateResult> SetEnabledFromOwnerAsync(
        CompanyId companyId,
        string moduleKey,
        bool enabled,
        string reason,
        UserId actorUserId,
        CancellationToken cancellationToken);
}
