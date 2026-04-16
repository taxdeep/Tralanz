namespace Citus.Platform.Core.Accounts;

public sealed record class PlatformMfaRecoveryRequestResult
{
    public Guid RequestId { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset RequestedAtUtc { get; init; }

    public PlatformAccountProfileSummary Profile { get; init; } = new();
}
