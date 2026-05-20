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

    /// <summary>
    /// Atomically transfers ownership from one membership to another
    /// inside the same company. The single UPDATE swaps both rows'
    /// <c>is_owner</c> flags and overwrites the new owner's
    /// <c>permissions</c> with <paramref name="newOwnerPermissions"/>
    /// (the workflow passes <c>CompanyMembershipPermissionPresets.Owner</c>
    /// expansion). The previous owner's permissions are left intact —
    /// callers wanting to revoke them must do so as a separate step.
    /// Throws when either membership is missing, when they belong to
    /// different companies, when either is inactive, or when from/to
    /// are the same row.
    /// </summary>
    Task<CompanyMembershipOwnershipTransferResult?> TransferOwnershipFromSysAdminAsync(
        CompanyId companyId,
        Guid fromMembershipId,
        Guid toMembershipId,
        string reason,
        UserId? sysAdminAccountId,
        IReadOnlyList<string> newOwnerPermissions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Business-side equivalent of
    /// <see cref="TransferOwnershipFromSysAdminAsync"/>. Resolves both
    /// memberships by <see cref="UserId"/> (the shape the business
    /// layer naturally has from the session), locks both rows, swaps
    /// <c>is_owner</c> + <c>role</c> + <c>permissions</c> in a single
    /// UPDATE, and appends an audit row with
    /// <c>actor_type='business_user'</c>. Throws if the caller is not
    /// the current owner of this company, if the target is inactive,
    /// or if either user is not a member.
    /// </summary>
    Task<CompanyMembershipOwnershipTransferResult?> TransferOwnershipFromOwnerAsync(
        CompanyId companyId,
        UserId currentOwnerUserId,
        UserId targetUserId,
        string reason,
        IReadOnlyList<string> newOwnerPermissions,
        CancellationToken cancellationToken);
}
