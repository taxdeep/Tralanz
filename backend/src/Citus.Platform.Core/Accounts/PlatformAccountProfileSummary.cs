namespace Citus.Platform.Core.Accounts;

public sealed record class PlatformAccountProfileSummary
{
    public Guid UserId { get; init; }

    public string Username { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset? EmailVerifiedAtUtc { get; init; }

    public bool NotificationVerificationReady { get; init; }

    public string NotificationBlockingReason { get; init; } = string.Empty;

    public string PendingEmailChangeMaskedDestination { get; init; } = string.Empty;

    public DateTimeOffset? PendingEmailChangeExpiresAtUtc { get; init; }

    public string PendingPasswordChangeMaskedDestination { get; init; } = string.Empty;

    public DateTimeOffset? PendingPasswordChangeExpiresAtUtc { get; init; }

    public bool IsEmailVerified => EmailVerifiedAtUtc.HasValue;

    public bool HasPendingEmailChange =>
        !string.IsNullOrWhiteSpace(PendingEmailChangeMaskedDestination) &&
        PendingEmailChangeExpiresAtUtc.HasValue;

    public bool HasPendingPasswordChange =>
        !string.IsNullOrWhiteSpace(PendingPasswordChangeMaskedDestination) &&
        PendingPasswordChangeExpiresAtUtc.HasValue;
}
