namespace Modules.CompanyAccess.Permissions;

/// <summary>
/// Write-side workflow for business-permission grants. Owner can
/// grant/revoke any assignable token; a non-Owner User can only
/// grant/revoke tokens they have explicit grant authority for.
///
/// All operations:
/// <list type="bullet">
///   <item>Run inside a single DB transaction (insert/update grant
///     row + write audit log).</item>
///   <item>Validate via <see cref="IPermissionEvaluator.CanGrantAsync"/>
///     before mutating.</item>
///   <item>Return a typed <see cref="PermissionMutationResult"/> on
///     both success and rejection so the calling endpoint can
///     surface the precise reason to the UI.</item>
/// </list>
///
/// Audit rows carry <c>actor_type='business_user'</c>,
/// <c>actor_id=actorUserId</c>, <c>entity_type='company_user_permission'</c>,
/// <c>action='permission_granted'</c> / <c>'permission_revoked'</c>,
/// and the full triple in the payload.
/// </summary>
public interface IPermissionGrantWorkflow
{
    /// <summary>Grant <paramref name="permissionToken"/> to <paramref name="targetUserId"/>.</summary>
    Task<PermissionMutationResult> GrantAsync(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        CancellationToken cancellationToken);

    /// <summary>Revoke <paramref name="permissionToken"/> from <paramref name="targetUserId"/>.</summary>
    Task<PermissionMutationResult> RevokeAsync(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Read-side: return the full set of active grants + grant
    /// authorities for a single user in this company. Callers must
    /// be the user themselves OR the company Owner (enforced at the
    /// endpoint layer; the workflow trusts callers).
    /// </summary>
    Task<UserPermissionSnapshot> GetUserPermissionsAsync(
        CompanyId companyId,
        UserId targetUserId,
        CancellationToken cancellationToken);
}
