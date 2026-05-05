namespace Modules.CompanyAccess.Memberships;

public interface ICompanyMembershipPermissionWorkflow
{
    IReadOnlyList<CompanyMembershipPermissionOption> GetAvailablePermissions();

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
}
