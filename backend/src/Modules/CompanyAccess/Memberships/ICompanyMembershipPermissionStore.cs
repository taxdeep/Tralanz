namespace Modules.CompanyAccess.Memberships;

public interface ICompanyMembershipPermissionStore
{
    Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
        Guid companyId,
        int limit,
        CancellationToken cancellationToken);

    Task<CompanyMembershipPermissionListItem?> GetAsync(
        Guid companyId,
        Guid membershipId,
        CancellationToken cancellationToken);

    Task<CompanyMembershipPermissionActorAuthority?> GetActorAuthorityAsync(
        Guid companyId,
        Guid actorUserId,
        CancellationToken cancellationToken);

    Task<CompanyMembershipPermissionListItem?> SavePermissionsAsync(
        Guid companyId,
        Guid membershipId,
        Guid actorUserId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken);
}
