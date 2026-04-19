namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformFirstCompanySetupState
{
    public const string PendingDecisionStatus = "pending";
    public const string DeferredDecisionStatus = "deferred";

    public string DecisionStatus { get; init; } = PendingDecisionStatus;

    public DateTimeOffset? DeferredAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsDeferred =>
        string.Equals(DecisionStatus, DeferredDecisionStatus, StringComparison.OrdinalIgnoreCase);
}
