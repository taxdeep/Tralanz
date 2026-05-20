namespace Modules.CompanyAccess.Permissions;

/// <summary>
/// Storage layer for business-permission grants. Holds the SQL.
/// Validation + audit composition live in the workflow.
/// </summary>
public interface IPermissionGrantStore
{
    /// <summary>
    /// Insert (or re-activate) an active grant row for
    /// (company, target, token). Returns true if a NEW row was
    /// produced (or a previously-revoked row was re-activated);
    /// false if an active row already existed (idempotent no-op).
    ///
    /// Same DB transaction also writes the audit row with
    /// <c>action='permission_granted'</c>.
    /// </summary>
    Task<bool> InsertGrantAsync(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Soft-revoke the active grant row for (company, target, token)
    /// by setting <c>is_active=false</c> + recording revoked_by /
    /// revoked_at. Returns true if a row was revoked; false if no
    /// active row existed (idempotent no-op).
    ///
    /// Same DB transaction also writes the audit row with
    /// <c>action='permission_revoked'</c>.
    /// </summary>
    Task<bool> MarkRevokedAsync(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Return all active grants + grant authorities + the Owner flag
    /// for a single user in a single company, in one round-trip.
    /// </summary>
    Task<UserPermissionSnapshot> ReadUserPermissionsAsync(
        CompanyId companyId,
        UserId targetUserId,
        CancellationToken cancellationToken);
}
