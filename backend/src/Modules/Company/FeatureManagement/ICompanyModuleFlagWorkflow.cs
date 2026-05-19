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
}
