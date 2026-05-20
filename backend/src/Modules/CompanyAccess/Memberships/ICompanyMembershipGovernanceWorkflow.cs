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

    /// <summary>
    /// Business-side ownership transfer: the current company Owner
    /// hands their status off to another active member of the same
    /// company. Mirrors the SysAdmin path but takes user ids (the
    /// shape the business UI naturally has from the session +
    /// UserPicker), records the audit log with
    /// <c>actor_type='business_user'</c>, and writes the
    /// <c>permission_grant_authority.assign</c> Owner-only action
    /// trail.
    ///
    /// <b>Authorization is NOT enforced here</b> — the calling endpoint
    /// must invoke
    /// <c>IPermissionEvaluator.CanPerformOwnerOnlyActionAsync</c> for
    /// <see cref="Permissions.OwnerOnlyActions.OwnerTransfer"/> first.
    /// This workflow trusts the caller's identity and enforces data
    /// invariants (caller must already be is_owner=true in the DB,
    /// target must be an active non-owner member, etc.) at the store
    /// layer.
    /// </summary>
    Task<CompanyMembershipOwnershipTransferResult> TransferOwnershipFromOwnerAsync(
        CompanyId companyId,
        UserId currentOwnerUserId,
        UserId targetUserId,
        string reason,
        CancellationToken cancellationToken);
}
