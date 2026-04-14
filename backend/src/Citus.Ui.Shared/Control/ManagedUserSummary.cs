namespace Citus.Ui.Shared.Control;

public sealed record class ManagedUserSummary
{
    public Guid Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public bool IsSysAdmin { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CompanyCodes { get; init; } = Array.Empty<string>();
}
