namespace Citus.Platform.Core.Runtime;

public sealed record class PasswordResetNotificationMessage
{
    public Guid DispatchId { get; init; }

    public string Destination { get; init; } = string.Empty;

    public string RecipientDisplayName { get; init; } = string.Empty;

    public string VerificationCode { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; }
}
