namespace Citus.Accounting.Api;

/// <summary>
/// Body shape for <c>POST /memberships/{userId}/permissions/grant</c>
/// and <c>POST /memberships/{userId}/permissions/revoke</c>. The
/// target user_id lives in the URL; the body carries company
/// context (via <see cref="BusinessRequestContractGuard"/>) and the
/// token to act on.
/// </summary>
public sealed record PermissionMutationHttpRequest(
    CompanyId CompanyId,
    string PermissionToken);
