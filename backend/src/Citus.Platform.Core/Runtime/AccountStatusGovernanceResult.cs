namespace Citus.Platform.Core.Runtime;

public sealed record class AccountStatusGovernanceResult
{
    public Guid AccountId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string PreviousStatus { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset? LockedUntilUtc { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; init; }
}
