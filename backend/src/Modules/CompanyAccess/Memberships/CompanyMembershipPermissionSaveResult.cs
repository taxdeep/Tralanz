namespace Modules.CompanyAccess.Memberships;

public sealed record class CompanyMembershipPermissionSaveResult(
    CompanyMembershipPermissionListItem Membership,
    IReadOnlyList<CompanyMembershipPermissionOption> AvailablePermissions,
    string OutcomeCode,
    string Message);
