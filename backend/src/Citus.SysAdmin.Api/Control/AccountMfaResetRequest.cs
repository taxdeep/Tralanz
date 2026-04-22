namespace Citus.SysAdmin.Api.Control;

public sealed record class AccountMfaResetRequest
{
    public string Reason { get; init; } = string.Empty;

    public Guid? SysAdminAccountId { get; init; }
}
