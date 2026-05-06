using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformGovernanceRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformAuditEvent>> ListRecentAuditEventsAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformAuditEvent>> ListAccountMfaTimelineAsync(
        UserId accountId,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ManagedPlatformAccountSummary>> ListManagedUsersAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MfaRecoveryRequestSummary>> ListOpenMfaRecoveryRequestsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MfaRecoveryRequestSummary>> ListAccountMfaRecoveryHistoryAsync(
        UserId accountId,
        int limit,
        CancellationToken cancellationToken);

    Task<CompanyStatusGovernanceResult?> SetCompanyStatusAsync(
        CompanyId companyId,
        string status,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<AccountStatusGovernanceResult?> SetAccountStatusAsync(
        UserId accountId,
        string status,
        DateTimeOffset? lockedUntilUtc,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<PasswordResetGovernanceResult?> RequestPasswordResetAsync(
        UserId accountId,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<AccountMfaResetGovernanceResult?> ResetAccountMfaAsync(
        UserId accountId,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<MfaRecoveryReviewResult?> ReviewMfaRecoveryRequestAsync(
        Guid requestId,
        string decision,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<MfaRecoveryExecutionResult?> ExecuteMfaRecoveryRequestAsync(
        Guid requestId,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken);
}
