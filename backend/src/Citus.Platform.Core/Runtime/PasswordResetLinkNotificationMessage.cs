namespace Citus.Platform.Core.Runtime;

/// <summary>
/// Self-serve password-reset email payload. Carries a fully-formed
/// reset URL the recipient clicks; the URL embeds an opaque token
/// the server validates server-side before accepting a new password.
/// </summary>
public sealed record class PasswordResetLinkNotificationMessage
{
    public Guid DispatchId { get; init; }

    public string Destination { get; init; } = string.Empty;

    public string RecipientDisplayName { get; init; } = string.Empty;

    public string ResetUrl { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; }
}
