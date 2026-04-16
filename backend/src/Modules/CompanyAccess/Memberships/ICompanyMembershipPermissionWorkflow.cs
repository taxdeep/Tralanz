namespace Modules.CompanyAccess.Memberships;

public interface ICompanyMembershipPermissionWorkflow
{
    IReadOnlyList<CompanyMembershipPermissionOption> GetAvailablePermissions();

    Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
        Guid companyId,
        int limit,
        CancellationToken cancellationToken);

    Task<CompanyMembershipPermissionSaveResult> SavePermissionsAsync(
        Guid companyId,
        Guid membershipId,
        Guid actorUserId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken);
}
