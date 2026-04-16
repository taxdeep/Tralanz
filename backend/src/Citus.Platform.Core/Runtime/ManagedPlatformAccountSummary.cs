namespace Citus.Platform.Core.Runtime;

public sealed record class ManagedPlatformAccountSummary
{
    public Guid AccountId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string MfaMode { get; init; } = "none";

    public DateTimeOffset? LastMfaResetAtUtc { get; init; }

    public string LastMfaResetReason { get; init; } = string.Empty;

    public IReadOnlyList<string> CompanyCodes { get; init; } = Array.Empty<string>();
}
