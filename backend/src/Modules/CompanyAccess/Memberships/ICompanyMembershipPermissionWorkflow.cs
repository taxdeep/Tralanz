namespace Modules.CompanyAccess.Memberships;

public interface ICompanyMembershipPermissionWorkflow
{
    IReadOnlyList<CompanyMembershipPermissionOption> GetAvailablePermissions();

    IReadOnlyList<CompanyMembershipPermissionPresetOption> GetAvailablePresets();

    Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
        CompanyId companyId,
        int limit,
        CancellationToken cancellationToken);

    Task<CompanyMembershipPermissionSaveResult> SavePermissionsAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId actorUserId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the membership's permission tokens with the union of
    /// the preset's expansion and any tokens already present that
    /// aren't part of the preset (so the operator's manual additions
    /// aren't lost on a re-apply). The actor must satisfy the same
    /// authority bar as <see cref="SavePermissionsAsync"/>.
    /// </summary>
    Task<CompanyMembershipPermissionSaveResult> ApplyPresetAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId actorUserId,
        string presetCode,
        bool replaceExistingTokens,
        CancellationToken cancellationToken);

    /// <summary>
    /// SysAdmin governance pathway for preset application. Bypasses
    /// the in-company actor check because the SysAdmin operator is
    /// not a company member; SysAdmin authority is implicit at the
    /// platform tier. Audit row is tagged with <c>actor_type='sysadmin'</c>.
    /// </summary>
    Task<CompanyMembershipPermissionSaveResult> ApplyPresetFromSysAdminAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId? sysAdminAccountId,
        string presetCode,
        bool replaceExistingTokens,
        CancellationToken cancellationToken);
}
