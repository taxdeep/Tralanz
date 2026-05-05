namespace Modules.CompanyAccess.Memberships;

public interface ICompanyMembershipPermissionStore
{
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
}
