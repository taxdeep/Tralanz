namespace Citus.SysAdmin.Api.Control;

public sealed record class CompanyMembershipRoleUpdateRequest
{
    public string Role { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public Guid? SysAdminAccountId { get; init; }
}
