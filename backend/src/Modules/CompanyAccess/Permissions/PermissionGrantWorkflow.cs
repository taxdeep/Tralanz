namespace Modules.CompanyAccess.Permissions;

/// <summary>
/// Default <see cref="IPermissionGrantWorkflow"/> wiring: pre-check
/// authorization via <see cref="IPermissionEvaluator.CanGrantAsync"/>,
/// then call the store. The eight hard rules of CanGrant cover all
/// the rejection paths (target is Owner, self-grant, etc.).
/// </summary>
public sealed class PermissionGrantWorkflow(
    IPermissionEvaluator evaluator,
    IPermissionGrantStore store) : IPermissionGrantWorkflow
{
    public async Task<PermissionMutationResult> GrantAsync(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        CancellationToken cancellationToken)
    {
        var (decision, message) = await EvaluateGrantAsync(
            companyId, actorUserId, targetUserId, permissionToken, cancellationToken);

        if (decision != GrantAuthorityResult.Allowed)
        {
            return BuildResult(companyId, actorUserId, targetUserId, permissionToken,
                action: "grant", applied: false, code: decision, message: message);
        }

        var inserted = await store.InsertGrantAsync(
            companyId, actorUserId, targetUserId, permissionToken, cancellationToken);

        return BuildResult(companyId, actorUserId, targetUserId, permissionToken,
            action: "grant",
            applied: inserted,
            code: GrantAuthorityResult.Allowed,
            message: inserted
                ? "Permission granted."
                : "Grant already active for this user (idempotent no-op).");
    }

    public async Task<PermissionMutationResult> RevokeAsync(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        CancellationToken cancellationToken)
    {
        // V1 reuses CanGrantAsync as the gate for revoke: if you can
        // grant a token, you can revoke it. The DB separately tracks
        // `can_revoke` for future refinement (e.g. grant-only auditor
        // patterns), but the first-cut UX binds them together and
        // there's no user-facing distinction yet.
        //
        // Owner bypasses, so Owner can revoke anything.
        var (decision, message) = await EvaluateGrantAsync(
            companyId, actorUserId, targetUserId, permissionToken, cancellationToken);

        if (decision != GrantAuthorityResult.Allowed)
        {
            return BuildResult(companyId, actorUserId, targetUserId, permissionToken,
                action: "revoke", applied: false, code: decision, message: message);
        }

        var revoked = await store.MarkRevokedAsync(
            companyId, actorUserId, targetUserId, permissionToken, cancellationToken);

        return BuildResult(companyId, actorUserId, targetUserId, permissionToken,
            action: "revoke",
            applied: revoked,
            code: GrantAuthorityResult.Allowed,
            message: revoked
                ? "Permission revoked."
                : "No active grant to revoke (idempotent no-op).");
    }

    public Task<UserPermissionSnapshot> GetUserPermissionsAsync(
        CompanyId companyId,
        UserId targetUserId,
        CancellationToken cancellationToken) =>
        store.ReadUserPermissionsAsync(companyId, targetUserId, cancellationToken);

    private async Task<(GrantAuthorityResult Decision, string Message)> EvaluateGrantAsync(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            return (GrantAuthorityResult.DeniedActorNotActiveMember,
                "Company context is required.");
        }

        if (string.IsNullOrEmpty(actorUserId.Value))
        {
            return (GrantAuthorityResult.DeniedActorNotActiveMember,
                "Actor identity is required.");
        }

        if (string.IsNullOrEmpty(targetUserId.Value))
        {
            return (GrantAuthorityResult.DeniedTargetNotActiveMember,
                "Target user is required.");
        }

        if (string.IsNullOrWhiteSpace(permissionToken))
        {
            return (GrantAuthorityResult.DeniedTokenNotInRegistry,
                "Permission token is required.");
        }

        var decision = await evaluator.CanGrantAsync(
            companyId, actorUserId, targetUserId, permissionToken, cancellationToken);

        var msg = decision switch
        {
            GrantAuthorityResult.Allowed
                => string.Empty,
            GrantAuthorityResult.DeniedSelfGrant
                => "You cannot modify your own permissions.",
            GrantAuthorityResult.DeniedActorNotActiveMember
                => "You are not an active member of this company.",
            GrantAuthorityResult.DeniedTargetNotActiveMember
                => "Target user is not an active member of this company.",
            GrantAuthorityResult.DeniedTargetIsOwner
                => "Cannot modify the company owner's permissions. Transfer ownership instead.",
            GrantAuthorityResult.DeniedTokenNotInRegistry
                => "Unknown permission token.",
            GrantAuthorityResult.DeniedTokenNotAssignable
                => "This permission is reserved (Owner-only or non-assignable).",
            GrantAuthorityResult.DeniedActorMissingGrantAuthority
                => "You do not have authority to grant or revoke this permission. Ask the company owner.",
            _ => "Permission change rejected.",
        };

        return (decision, msg);
    }

    private static PermissionMutationResult BuildResult(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        string action,
        bool applied,
        GrantAuthorityResult code,
        string message) => new()
    {
        CompanyId = companyId,
        ActorUserId = actorUserId,
        TargetUserId = targetUserId,
        PermissionToken = permissionToken,
        Action = action,
        Applied = applied,
        ResultCode = code,
        ResultMessage = message,
    };
}
