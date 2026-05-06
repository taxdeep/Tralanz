namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// Self-serve password reset for Business shell accounts. Two-step
/// flow:
///
///   1. <see cref="IssueTokenAsync"/> — operator submits their email
///      from the Forgot Password page. We always behave the same
///      whether the account exists or not (no enumeration). When the
///      account exists, a single-use token is hashed + stored,
///      paired with a 15-minute expiry, and returned in plaintext
///      exactly once for the caller to embed in the email URL.
///
///   2. <see cref="RedeemTokenAsync"/> — operator clicks the link,
///      types a new password. Server verifies the token (active +
///      not expired + not yet used), hashes the new password,
///      writes it to users.password_hash, marks the token consumed,
///      and revokes every active session for that account so other
///      devices are forced to re-authenticate.
///
/// SysAdmin-driven password reset (the existing flow keyed on a
/// 6-digit code) is unchanged — see PostgresPlatformGovernanceRepository.
/// </summary>
public interface IPlatformBusinessPasswordResetService
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the plaintext token + recipient context if the account
    /// exists and is eligible. Returns null when no account matches —
    /// callers should return a 200 anyway to prevent enumeration.
    /// </summary>
    Task<PasswordResetIssueResult?> IssueTokenAsync(
        string email,
        string? requestedIp,
        CancellationToken cancellationToken);

    Task<PasswordResetRedeemResult> RedeemTokenAsync(
        string plaintextToken,
        string newPassword,
        CancellationToken cancellationToken);
}

public sealed record class PasswordResetIssueResult(
    string PlaintextToken,
    UserId AccountId,
    string Email,
    string DisplayName,
    DateTimeOffset ExpiresAtUtc);

public sealed record class PasswordResetRedeemResult(
    bool Succeeded,
    string? FailureCode,
    string? FailureMessage);
