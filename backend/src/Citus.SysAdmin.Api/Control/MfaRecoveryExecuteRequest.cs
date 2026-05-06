namespace Citus.SysAdmin.Api.Control;

public sealed record class MfaRecoveryExecuteRequest
{
    public string Reason { get; init; } = string.Empty;

    public UserId? SysAdminAccountId { get; init; }
}
