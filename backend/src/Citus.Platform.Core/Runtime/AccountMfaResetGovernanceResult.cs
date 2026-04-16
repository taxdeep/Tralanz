namespace Citus.Platform.Core.Runtime;

public sealed record class AccountMfaResetGovernanceResult
{
    public Guid AccountId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string PreviousMfaMode { get; init; } = string.Empty;

    public string MfaMode { get; init; } = "none";

    public int RevokedChallengeCount { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; init; }
}
