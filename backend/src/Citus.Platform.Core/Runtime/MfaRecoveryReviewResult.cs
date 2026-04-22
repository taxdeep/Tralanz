namespace Citus.Platform.Core.Runtime;

public sealed record class MfaRecoveryReviewResult
{
    public Guid RequestId { get; init; }

    public Guid AccountId { get; init; }

    public string Status { get; init; } = string.Empty;

    public string ReviewReason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAtUtc { get; init; }
}
