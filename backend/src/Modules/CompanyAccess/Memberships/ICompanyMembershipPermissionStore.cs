namespace Modules.CompanyAccess.Memberships;

public interface ICompanyMembershipPermissionStore
{
    /// <summary>
    /// Idempotent one-time migration that expands every legacy
    /// permission token on every membership into its fine-grained
    /// equivalents (see
    /// <see cref="CompanyMembershipPermissionLegacyExpansion"/>).
    /// Safe to call on every startup — rows already expanded are
    /// detected and skipped, so re-runs do no DB writes.
    /// </summary>
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
        CompanyId companyId,
        int limit,
        CancellationToken cancellationToken);

    Task<CompanyMembershipPermissionListItem?> GetAsync(
        CompanyId companyId,
        Guid membershipId,
        CancellationToken cancellationToken);

    Task<CompanyMembershipPermissionActorAuthority?> GetActorAuthorityAsync(
        CompanyId companyId,
        UserId actorUserId,
        CancellationToken cancellationToken);

    Task<CompanyMembershipPermissionListItem?> SavePermissionsAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId actorUserId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken);

    /// <summary>
    /// SysAdmin write-side companion to
    /// <see cref="SavePermissionsAsync"/>. Same persistence; the audit
    /// row records <c>actor_type='sysadmin'</c> instead of
    /// <c>'user'</c>, and <paramref name="sysAdminAccountId"/> may be
    /// null (the audit row carries a null actor_id in that case).
    /// </summary>
    Task<CompanyMembershipPermissionListItem?> SavePermissionsFromSysAdminAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId? sysAdminAccountId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken);
}
