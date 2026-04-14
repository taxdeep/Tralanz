namespace Citus.Ui.Shared.Control;

public sealed record class SysAdminOperatorSummary
{
    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
}
