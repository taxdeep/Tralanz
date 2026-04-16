namespace Citus.Platform.Core.Runtime;

public sealed record class PasswordResetGovernanceResult
{
    public Guid RequestId { get; init; }

    public Guid AccountId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string DeliveryStatus { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset RequestedAtUtc { get; init; }
}
