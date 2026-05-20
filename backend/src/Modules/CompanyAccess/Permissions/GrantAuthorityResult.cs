namespace Modules.CompanyAccess.Permissions;

/// <summary>
/// Outcome of <see cref="IPermissionEvaluator.CanGrantAsync"/>. The
/// enum is intentionally narrow: each value is a stable
/// machine-readable reason that the UI / audit log can render and
/// that tests can assert against. New denial categories should be
/// added as new enum values rather than overloading existing ones.
/// </summary>
public enum GrantAuthorityResult
{
    /// <summary>Actor is allowed to grant or revoke the token to the target.</summary>
    Allowed,

    /// <summary>Actor is not an active member of the company.</summary>
    DeniedActorNotActiveMember,

    /// <summary>Target is not an active member of the company.</summary>
    DeniedTargetNotActiveMember,

    /// <summary>Target is the Owner — non-Owner cannot modify Owner permissions; Owner cannot modify their own implied-all set this way.</summary>
    DeniedTargetIsOwner,

    /// <summary>Actor tried to grant to themselves. First version blocks unconditionally to prevent self-promotion.</summary>
    DeniedSelfGrant,

    /// <summary>Token is not registered in permission_registry.</summary>
    DeniedTokenNotInRegistry,

    /// <summary>Token exists but is not assignable (e.g. one of the four Owner-only actions).</summary>
    DeniedTokenNotAssignable,

    /// <summary>Actor is a non-Owner without an active grant-authority row for this token (or with can_grant=false).</summary>
    DeniedActorMissingGrantAuthority,
}
