namespace Modules.CompanyAccess.Memberships;

public sealed record class CompanyMembershipPermissionActorAuthority(
    Guid CompanyId,
    Guid UserId,
    string Role,
    IReadOnlyList<string> PermissionTokens)
{
    public bool CanManageMembershipPermissions =>
        string.Equals(Role, "owner", StringComparison.Ordinal);
}
