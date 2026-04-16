namespace Citus.Platform.Core.Accounts;

public sealed record class PlatformAccountProfileSummary
{
    public Guid UserId { get; init; }

    public string Username { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset? EmailVerifiedAtUtc { get; init; }

    public string MfaMode { get; init; } = "none";

    public DateTimeOffset? LastMfaModeChangedAtUtc { get; init; }

    public string PreviousMfaMode { get; init; } = string.Empty;

    public DateTimeOffset? LastMfaResetAtUtc { get; init; }

    public string LastMfaResetReason { get; init; } = string.Empty;

    public string LastMfaResetByDisplayName { get; init; } = string.Empty;

    public Guid? ActiveMfaRecoveryRequestId { get; init; }

    public string ActiveMfaRecoveryStatus { get; init; } = string.Empty;

    public DateTimeOffset? ActiveMfaRecoveryRequestedAtUtc { get; init; }

    public string ActiveMfaRecoveryRequestReason { get; init; } = string.Empty;

    public DateTimeOffset? ActiveMfaRecoveryReviewedAtUtc { get; init; }

    public string ActiveMfaRecoveryReviewReason { get; init; } = string.Empty;

    public string ActiveMfaRecoveryReviewedByDisplayName { get; init; } = string.Empty;

    public bool NotificationVerificationReady { get; init; }

    public string NotificationBlockingReason { get; init; } = string.Empty;

    public string PendingEmailChangeMaskedDestination { get; init; } = string.Empty;

    public DateTimeOffset? PendingEmailChangeExpiresAtUtc { get; init; }

    public string PendingPasswordChangeMaskedDestination { get; init; } = string.Empty;

    public DateTimeOffset? PendingPasswordChangeExpiresAtUtc { get; init; }

    public bool IsEmailVerified => EmailVerifiedAtUtc.HasValue;

    public bool IsMfaEnabled =>
        !string.Equals(MfaMode, "none", StringComparison.OrdinalIgnoreCase);

    public string MfaModeLabel =>
        MfaMode.Trim().ToLowerInvariant() switch
        {
            "email_code" => "Email verification code",
            _ => "Disabled"
        };

    public string PreviousMfaModeLabel =>
        PreviousMfaMode.Trim().ToLowerInvariant() switch
        {
            "email_code" => "Email verification code",
            "none" => "Disabled",
            _ => string.Empty
        };

    public bool HasPendingEmailChange =>
        !string.IsNullOrWhiteSpace(PendingEmailChangeMaskedDestination) &&
        PendingEmailChangeExpiresAtUtc.HasValue;

    public bool HasPendingPasswordChange =>
        !string.IsNullOrWhiteSpace(PendingPasswordChangeMaskedDestination) &&
        PendingPasswordChangeExpiresAtUtc.HasValue;

    public bool HasRecordedMfaReset => LastMfaResetAtUtc.HasValue;

    public bool HasActiveMfaRecoveryRequest =>
        ActiveMfaRecoveryRequestId.HasValue &&
        !string.IsNullOrWhiteSpace(ActiveMfaRecoveryStatus);
}
