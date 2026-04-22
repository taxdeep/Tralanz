namespace Citus.SysAdmin.Api.Control;

public sealed record class AccountStatusUpdateRequest
{
    public string Status { get; init; } = string.Empty;

    public DateTimeOffset? LockedUntilUtc { get; init; }

    public string Reason { get; init; } = string.Empty;

    public Guid? SysAdminAccountId { get; init; }
}
