namespace Citus.Ui.Shared.Control;

public sealed record class SysAdminAuthSessionSummary
{
    public UserId SysAdminAccountId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public DateTimeOffset ExpiresAtUtc { get; init; }
}
