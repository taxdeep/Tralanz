namespace Modules.CompanyAccess.Permissions;

/// <summary>
/// A single business-permission grant row for a (company, user, token)
/// triple. Active rows participate in <see cref="IPermissionEvaluator.CanAsync"/>;
/// revoked rows are kept for audit recoverability.
/// </summary>
public sealed record class PermissionGrant
{
    public required CompanyId CompanyId { get; init; }

    public required UserId UserId { get; init; }

    public required string PermissionToken { get; init; }

    public required UserId GrantedByUserId { get; init; }

    public required DateTimeOffset GrantedAtUtc { get; init; }

    public UserId? RevokedByUserId { get; init; }

    public DateTimeOffset? RevokedAtUtc { get; init; }

    public required bool IsActive { get; init; }
}

/// <summary>
/// A delegated grant authority: User A is allowed to grant or revoke
/// <see cref="GrantablePermissionToken"/> on behalf of OTHER users in
/// the same company. Owner-only assignment (anti-recursion: cannot be
/// re-delegated by a non-Owner).
/// </summary>
public sealed record class PermissionGrantAuthority
{
    public required CompanyId CompanyId { get; init; }

    public required UserId UserId { get; init; }

    public required string GrantablePermissionToken { get; init; }

    public required bool CanGrant { get; init; }

    public required bool CanRevoke { get; init; }

    public required UserId GrantedByOwnerUserId { get; init; }

    public required DateTimeOffset GrantedAtUtc { get; init; }

    public UserId? RevokedByOwnerUserId { get; init; }

    public DateTimeOffset? RevokedAtUtc { get; init; }

    public required bool IsActive { get; init; }
}

/// <summary>
/// Read-side snapshot of every active permission grant + grant
/// authority a user holds in a single company. Used by the
/// management UI to render the per-user permission matrix.
/// </summary>
public sealed record class UserPermissionSnapshot
{
    public required CompanyId CompanyId { get; init; }

    public required UserId UserId { get; init; }

    /// <summary>True if the user is the company Owner (implied-all-permissions).</summary>
    public required bool IsOwner { get; init; }

    public IReadOnlyList<PermissionGrant> ActiveGrants { get; init; } = Array.Empty<PermissionGrant>();

    public IReadOnlyList<PermissionGrantAuthority> ActiveGrantAuthorities { get; init; } = Array.Empty<PermissionGrantAuthority>();
}

/// <summary>
/// Outcome of a single grant / revoke operation.
/// <see cref="Applied"/> is true on success;
/// <see cref="ResultCode"/> mirrors <see cref="GrantAuthorityResult"/>
/// on denial so the caller (endpoint + UI) can render a precise
/// error.
/// </summary>
public sealed record class PermissionMutationResult
{
    public required CompanyId CompanyId { get; init; }

    public required UserId ActorUserId { get; init; }

    public required UserId TargetUserId { get; init; }

    public required string PermissionToken { get; init; }

    public required string Action { get; init; } // "grant" | "revoke"

    public required bool Applied { get; init; }

    public required GrantAuthorityResult ResultCode { get; init; }

    public string ResultMessage { get; init; } = string.Empty;
}
