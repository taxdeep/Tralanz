namespace Citus.Platform.Core.Runtime;

public sealed record class MfaRecoveryExecutionResult
{
    public Guid RequestId { get; init; }

    public UserId AccountId { get; init; }

    public string PreviousMfaMode { get; init; } = string.Empty;

    public string MfaMode { get; init; } = "none";

    public int RevokedChallengeCount { get; init; }

    public string ExecutionReason { get; init; } = string.Empty;

    public DateTimeOffset ExecutedAtUtc { get; init; }
}
