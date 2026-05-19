namespace Modules.CompanyAccess.Memberships;

public interface ICompanyMembershipGovernanceWorkflow
{
    Task<CompanyMembershipRoleChangeResult> ChangeRoleFromSysAdminAsync(
        CompanyId companyId,
        Guid membershipId,
        string role,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);

    /// <summary>
    /// SysAdmin governance pathway for ownership transfer. Expands
    /// the <c>preset.owner</c> token set and delegates to the store
    /// for atomic execution. New owner ends with full catalog
    /// permissions; previous owner retains their existing token set
    /// minus the owner flag.
    /// </summary>
    Task<CompanyMembershipOwnershipTransferResult> TransferOwnershipFromSysAdminAsync(
        CompanyId companyId,
        Guid fromMembershipId,
        Guid toMembershipId,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);
}
