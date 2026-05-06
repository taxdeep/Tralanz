namespace Citus.SysAdmin.Api.Control;

public sealed record class CompanyStatusUpdateRequest
{
    public string Status { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public UserId? SysAdminAccountId { get; init; }
}
