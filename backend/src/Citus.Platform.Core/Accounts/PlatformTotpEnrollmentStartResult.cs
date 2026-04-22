namespace Citus.Platform.Core.Accounts;

public sealed record class PlatformTotpEnrollmentStartResult
{
    public Guid EnrollmentId { get; init; }

    public string Factor { get; init; } = "totp_app";

    public string Issuer { get; init; } = string.Empty;

    public string AccountLabel { get; init; } = string.Empty;

    public string SecretBase32 { get; init; } = string.Empty;

    public string OtpAuthUri { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public PlatformAccountProfileSummary Profile { get; init; } = new();
}
