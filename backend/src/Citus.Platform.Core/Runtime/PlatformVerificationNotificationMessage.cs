namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformVerificationNotificationMessage
{
    public Guid DispatchId { get; init; }

    public Guid UserId { get; init; }

    public string Purpose { get; init; } = string.Empty;

    public string Destination { get; init; } = string.Empty;

    public string RecipientDisplayName { get; init; } = string.Empty;

    public string VerificationCode { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; }
}
