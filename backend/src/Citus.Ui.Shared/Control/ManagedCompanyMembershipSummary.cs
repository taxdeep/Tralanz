namespace Citus.Ui.Shared.Control;

public sealed record class ManagedCompanyMembershipSummary
{
    public Guid MembershipId { get; init; }

    public Guid CompanyId { get; init; }

    public Guid AccountId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public IReadOnlyList<string> PermissionTokens { get; init; } = Array.Empty<string>();

    public bool IsActive { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
