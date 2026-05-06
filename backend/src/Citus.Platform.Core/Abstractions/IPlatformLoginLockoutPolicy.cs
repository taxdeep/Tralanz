namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// Brute-force protection that both shells (SysAdmin + Business) call
/// from their <c>AuthenticateAsync</c> path. Tracks failed login
/// attempts keyed on a SHA-256 hash of the lowercased email, applies a
/// two-tier lockout policy:
///
///   * 5 failed attempts in 15 minutes → 15-minute temporary lockout.
///   * 3 temporary lockouts in 36 hours → permanent lockout (sets the
///     account's status to 'locked'); only a SysAdmin can lift it via
///     the Locked Accounts page or the emergency CLI.
///
/// Failure counts are scoped per realm so a SysAdmin attack doesn't
/// lock the same email out of the Business shell, and so the SysAdmin
/// auditor can see at a glance which surface is being targeted.
/// </summary>
public interface IPlatformLoginLockoutPolicy
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Called before password verification. Returns
    /// <see cref="LoginLockoutCheck.IsBlocked"/>=true when a lockout
    /// is currently active for this realm + email.
    /// </summary>
    Task<LoginLockoutCheck> CheckAsync(
        string realm,
        string email,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called after password verification (whether it succeeded or
    /// failed). Records the attempt, then on failure runs the threshold
    /// math and inserts a temporary or permanent lockout if either
    /// trigger is hit.
    /// </summary>
    Task RecordAttemptAsync(
        LoginAttempt attempt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lockouts that are still in effect (no <c>lifted_at</c>, and
    /// either permanent or with <c>locked_until</c> in the future).
    /// Surfaces the SysAdmin "Locked Accounts" page.
    /// </summary>
    Task<IReadOnlyList<LockoutSummary>> ListActiveLockoutsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Manual unlock by a SysAdmin. Marks the lockout
    /// <c>lifted_at = now()</c>, records who did it and why. If the
    /// lockout was permanent and the account's <c>status</c> is
    /// 'locked', also flips it back to 'active'.
    /// </summary>
    Task<LockoutLiftResult> LiftLockoutAsync(
        Guid lockoutId,
        UserId sysAdminAccountId,
        string reason,
        CancellationToken cancellationToken);
}

public sealed record class LoginAttempt(
    string Realm,
    string Email,
    UserId? AccountId,
    string? RemoteIp,
    string? UserAgent,
    bool Succeeded);

public sealed record class LoginLockoutCheck(
    bool IsBlocked,
    string? BlockKind,
    DateTimeOffset? LockedUntil,
    string? Message);

public sealed record class LockoutSummary(
    Guid Id,
    string Realm,
    string MaskedEmail,
    UserId? AccountId,
    string LockoutKind,
    DateTimeOffset LockedAt,
    DateTimeOffset? LockedUntil,
    int RecentFailureCount);

public sealed record class LockoutLiftResult(
    bool Succeeded,
    string? FailureReason);

public static class LoginLockoutRealms
{
    public const string SysAdmin = "sysadmin";
    public const string Business = "business";

    public static bool IsValid(string value) =>
        value == SysAdmin || value == Business;
}

public static class LoginLockoutKinds
{
    public const string Temporary15Min = "temporary_15min";
    public const string Permanent = "permanent";
}
