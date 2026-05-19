namespace Modules.CompanyAccess.Memberships;

/// <summary>
/// Result of a successful ownership transfer. <see cref="FromMembershipId"/>
/// is the previous owner (now demoted, with their existing permission
/// tokens preserved); <see cref="ToMembershipId"/> is the new owner,
/// whose permissions have been overwritten with the
/// <c>CompanyMembershipPermissionPresets.Owner</c> expansion.
/// </summary>
public sealed record class CompanyMembershipOwnershipTransferResult
{
    public required CompanyId CompanyId { get; init; }

    public required Guid FromMembershipId { get; init; }

    public required UserId FromUserId { get; init; }

    public required Guid ToMembershipId { get; init; }

    public required UserId ToUserId { get; init; }

    public string Reason { get; init; } = string.Empty;

    public required DateTimeOffset TransferredAtUtc { get; init; }
}
