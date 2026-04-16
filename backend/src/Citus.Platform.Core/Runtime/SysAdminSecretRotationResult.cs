namespace Citus.Platform.Core.Runtime;

public sealed record class SysAdminSecretRotationResult
{
    public bool Succeeded { get; init; }

    public Guid SysAdminAccountId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public DateTimeOffset RotatedAtUtc { get; init; }

    public string FailureCode { get; init; } = string.Empty;

    public string FailureMessage { get; init; } = string.Empty;
}
