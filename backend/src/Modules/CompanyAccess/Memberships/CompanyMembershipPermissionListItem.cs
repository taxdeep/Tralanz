namespace Modules.CompanyAccess.Memberships;

public sealed record class CompanyMembershipPermissionListItem
{
    public Guid MembershipId { get; init; }

    public CompanyId CompanyId { get; init; }

    public UserId UserId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public IReadOnlyList<string> PermissionTokens { get; init; } = Array.Empty<string>();

    public bool IsActive { get; init; }

    /// <summary>
    /// True when this membership is the company's owner. Authoritative
    /// flag (driven by <c>company_memberships.is_owner</c>) — do NOT
    /// derive ownership from <see cref="Role"/>, which is now a display
    /// label only.
    /// </summary>
    public bool IsOwner { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
