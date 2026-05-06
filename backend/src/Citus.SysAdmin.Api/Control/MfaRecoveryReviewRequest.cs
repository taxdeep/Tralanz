namespace Citus.SysAdmin.Api.Control;

public sealed record class MfaRecoveryReviewRequest
{
    public string Decision { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public UserId? SysAdminAccountId { get; init; }
}
