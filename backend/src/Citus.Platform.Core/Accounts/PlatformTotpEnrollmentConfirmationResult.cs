namespace Citus.Platform.Core.Accounts;

public sealed record class PlatformTotpEnrollmentConfirmationResult
{
    public Guid EnrollmentId { get; init; }

    public string Factor { get; init; } = "totp_app";

    public DateTimeOffset ConfirmedAtUtc { get; init; }

    public PlatformAccountProfileSummary Profile { get; init; } = new();
}
