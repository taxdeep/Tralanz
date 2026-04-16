namespace Modules.CompanyAccess.Memberships;

public sealed record class CompanyMembershipRoleChangeResult
{
    public Guid CompanyId { get; init; }

    public Guid MembershipId { get; init; }

    public Guid AccountId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string PreviousRole { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; init; }
}
