namespace Modules.CompanyAccess.Memberships;

public sealed record class CompanyMembershipPermissionOption(
    string Token,
    string Label,
    string Description,
    bool IsGovernancePermission);
