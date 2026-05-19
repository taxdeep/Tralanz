namespace Citus.Ui.Shared.Control;

public sealed record class ManagedCompanyMembershipSummary
{
    public Guid MembershipId { get; init; }

    public CompanyId CompanyId { get; init; }

    public UserId AccountId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public IReadOnlyList<string> PermissionTokens { get; init; } = Array.Empty<string>();

    public bool IsActive { get; init; }

    /// <summary>
    /// Batch 3.5: authoritative owner flag. Do not derive from
    /// <see cref="Role"/> — the role string is a display label only.
    /// </summary>
    public bool IsOwner { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
