namespace Modules.CompanyAccess.Permissions;

/// <summary>
/// The four actions that only a company Owner can perform. These are
/// NOT assignable permission tokens — they're hard-coded checks
/// against <c>is_owner=true</c> on the caller's
/// <c>company_memberships</c> row.
///
/// They appear in <c>permission_registry</c> with
/// <c>is_assignable=false</c> so the UI can render them in the
/// permission catalog ("this exists, only Owner can do it"), but the
/// grant flow (<see cref="IPermissionEvaluator.CanGrantAsync"/>)
/// refuses them. Anti-recursion: even a User with grant authority
/// over other tokens cannot delegate these — only Owner can transfer
/// ownership, set company inactive, or assign/revoke grant authority.
/// </summary>
public static class OwnerOnlyActions
{
    public const string CompanyMakeInactive = "company.make_inactive";
    public const string OwnerTransfer = "owner.transfer";
    public const string GrantAuthorityAssign = "permission_grant_authority.assign";
    public const string GrantAuthorityRevoke = "permission_grant_authority.revoke";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        CompanyMakeInactive,
        OwnerTransfer,
        GrantAuthorityAssign,
        GrantAuthorityRevoke,
    };

    public static bool IsOwnerOnly(string? action) =>
        !string.IsNullOrWhiteSpace(action) && All.Contains(action);
}
