namespace Modules.CompanyAccess.Memberships;

public interface ICompanyMembershipGovernanceStore
{
    Task<CompanyMembershipRoleChangeResult?> ChangeRoleFromSysAdminAsync(
        CompanyId companyId,
        Guid membershipId,
        string role,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);
}
