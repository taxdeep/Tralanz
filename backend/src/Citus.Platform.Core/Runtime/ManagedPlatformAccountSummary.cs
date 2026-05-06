namespace Citus.Platform.Core.Runtime;

public sealed record class ManagedPlatformAccountSummary
{
    public UserId AccountId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string MfaMode { get; init; } = "none";

    public string ActiveMfaRecoveryStatus { get; init; } = string.Empty;

    public DateTimeOffset? LastMfaResetAtUtc { get; init; }

    public string LastMfaResetReason { get; init; } = string.Empty;

    public IReadOnlyList<string> CompanyCodes { get; init; } = Array.Empty<string>();

    public bool HasActiveMfaRecoveryRequest =>
        !string.IsNullOrWhiteSpace(ActiveMfaRecoveryStatus);

    public bool CanEmergencyMfaReset =>
        !string.Equals(MfaMode, "none", StringComparison.OrdinalIgnoreCase) &&
        !HasActiveMfaRecoveryRequest;

    public string EmergencyMfaResetPolicyReason =>
        string.Equals(MfaMode, "none", StringComparison.OrdinalIgnoreCase)
            ? "Emergency reset is unavailable because MFA is already disabled for this account."
            : HasActiveMfaRecoveryRequest
                ? "Emergency reset is blocked while a governed recovery request is still open for this account."
                : "Emergency reset is available only as a SysAdmin fallback outside the normal recovery request flow.";
}
