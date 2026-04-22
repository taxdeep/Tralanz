namespace Citus.Ui.Shared.Control;

public sealed record class MfaRecoveryRequestSummary
{
    public Guid RequestId { get; init; }

    public Guid AccountId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string CurrentMfaMode { get; init; } = "none";

    public string Status { get; init; } = string.Empty;

    public string RequestReason { get; init; } = string.Empty;

    public DateTimeOffset RequestedAtUtc { get; init; }

    public string ReviewReason { get; init; } = string.Empty;

    public DateTimeOffset? ReviewedAtUtc { get; init; }

    public string ReviewedByDisplayName { get; init; } = string.Empty;

    public string ExecutionReason { get; init; } = string.Empty;

    public DateTimeOffset? ExecutedAtUtc { get; init; }

    public string ExecutedByDisplayName { get; init; } = string.Empty;

    public string CurrentMfaModeLabel =>
        CurrentMfaMode.Trim().ToLowerInvariant() switch
        {
            "email_code" => "Email code",
            "totp_app" => "Authenticator app (TOTP)",
            _ => "Disabled"
        };
}
