namespace Modules.CompanyAccess.Memberships;

public sealed record class CompanyMembershipPermissionAuditRecord
{
    public Guid AuditId { get; init; }

    public Guid CompanyId { get; init; }

    public Guid MembershipId { get; init; }

    public Guid? ActorUserId { get; init; }

    public string ActorDisplayName { get; init; } = string.Empty;

    public string ActorEmail { get; init; } = string.Empty;

    public Guid? TargetUserId { get; init; }

    public string TargetDisplayName { get; init; } = string.Empty;

    public string TargetEmail { get; init; } = string.Empty;

    public string TargetRole { get; init; } = string.Empty;

    public IReadOnlyList<string> PreviousPermissionTokens { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SavedPermissionTokens { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AddedPermissionTokens { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RemovedPermissionTokens { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}
