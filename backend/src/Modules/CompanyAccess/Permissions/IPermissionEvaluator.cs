namespace Modules.CompanyAccess.Permissions;

/// <summary>
/// Read-side authorization service for the Tralanz permission model.
///
/// Model recap:
/// <list type="bullet">
///   <item><b>Owner</b> — unique transferable status per company.
///     Implied-all-permissions within the company. Sole bearer of the
///     four hard-coded Owner-only actions
///     (<see cref="OwnerOnlyActions"/>).</item>
///   <item><b>User</b> — ordinary active member. NO implicit role.
///     Permissions come from two orthogonal grants:
///     <list type="number">
///       <item><b>Business permission</b> — what the user can DO,
///         stored in <c>company_user_permissions</c>.</item>
///       <item><b>Grant authority</b> — which tokens the user can
///         GRANT or REVOKE on behalf of OTHER users, stored in
///         <c>company_user_permission_grant_authorities</c>. Having
///         grant authority for token X does NOT confer business
///         permission X.</item>
///     </list>
///   </item>
/// </list>
///
/// Authorization check order (callers may compose these):
/// <list type="number">
///   <item>company_id isolation (assumed; this service trusts the
///     CompanyId passed in is the caller's active company).</item>
///   <item>Active membership (<see cref="IsActiveMemberAsync"/>).</item>
///   <item>Module enabled (out-of-scope here; see module gate filters).</item>
///   <item>Business permission check (<see cref="CanAsync"/>) or
///     Owner-only action check
///     (<see cref="CanPerformOwnerOnlyActionAsync"/>).</item>
///   <item>Data scope (entity-specific; e.g. assignee-only Tasks).</item>
/// </list>
///
/// This is read-only: no inserts/updates here. Write paths (grant /
/// revoke / preset apply / owner transfer) live in subsequent PRs
/// and consume this evaluator for their preflight checks.
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>
    /// Is the user an active member of the company? Foundation for
    /// every other check.
    /// </summary>
    Task<bool> IsActiveMemberAsync(
        CompanyId companyId,
        UserId userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Is the user the (unique) active Owner of the company?
    /// Returns false for non-members, inactive members, and non-Owner
    /// active members.
    /// </summary>
    Task<bool> IsOwnerAsync(
        CompanyId companyId,
        UserId userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Can the actor perform one of the four Owner-only actions?
    /// Returns false unless <paramref name="ownerOnlyAction"/> is in
    /// <see cref="OwnerOnlyActions.All"/> AND the actor is the active
    /// Owner. There is no token-based path to true here — these
    /// actions cannot be delegated.
    /// </summary>
    Task<bool> CanPerformOwnerOnlyActionAsync(
        CompanyId companyId,
        UserId actorId,
        string ownerOnlyAction,
        CancellationToken cancellationToken);

    /// <summary>
    /// Can the actor perform a business action identified by token?
    /// Owner bypasses (implied-all); non-Owner needs an active row in
    /// <c>company_user_permissions</c> for that exact token. Returns
    /// false for Owner-only tokens (callers must use
    /// <see cref="CanPerformOwnerOnlyActionAsync"/>).
    /// </summary>
    Task<bool> CanAsync(
        CompanyId companyId,
        UserId actorId,
        string permissionToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Can the actor grant or revoke <paramref name="permissionToken"/>
    /// to <paramref name="targetId"/> inside the company?
    ///
    /// Owner bypasses (after the same-company / target-not-owner /
    /// not-self-grant / assignable-token checks). Non-Owner needs an
    /// active grant-authority row with <c>can_grant=true</c> for the
    /// token.
    ///
    /// The eight hard rules enforced here:
    /// <list type="number">
    ///   <item>cannot grant to Owner;</item>
    ///   <item>cannot grant to self (first version);</item>
    ///   <item>cannot grant to non-member of this company;</item>
    ///   <item>cannot grant to inactive member;</item>
    ///   <item>cannot grant an Owner-only action;</item>
    ///   <item>cannot grant a token absent from
    ///     <c>permission_registry</c>;</item>
    ///   <item>cannot grant a token the actor lacks grant authority
    ///     for (unless Owner);</item>
    ///   <item>cannot grant grant-authority itself — that path is
    ///     <see cref="OwnerOnlyActions.GrantAuthorityAssign"/>, owner
    ///     only.</item>
    /// </list>
    /// </summary>
    Task<GrantAuthorityResult> CanGrantAsync(
        CompanyId companyId,
        UserId actorId,
        UserId targetId,
        string permissionToken,
        CancellationToken cancellationToken);
}
