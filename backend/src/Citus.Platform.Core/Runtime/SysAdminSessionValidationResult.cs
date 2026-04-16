namespace Citus.Platform.Core.Runtime;

public sealed record class SysAdminSessionValidationResult
{
    public bool Succeeded { get; init; }

    public string FailureCode { get; init; } = string.Empty;

    public string FailureMessage { get; init; } = string.Empty;

    public Guid SysAdminAccountId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public DateTimeOffset ExpiresAtUtc { get; init; }
}
