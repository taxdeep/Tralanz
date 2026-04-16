namespace Citus.SysAdmin.Api.Control;

public sealed record class PasswordResetRequestCommand
{
    public string Reason { get; init; } = string.Empty;

    public Guid? SysAdminAccountId { get; init; }
}
