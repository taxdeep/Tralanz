using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformGovernanceRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformAuditEvent>> ListRecentAuditEventsAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformAuditEvent>> ListAccountMfaTimelineAsync(
        Guid accountId,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ManagedPlatformAccountSummary>> ListManagedUsersAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MfaRecoveryRequestSummary>> ListOpenMfaRecoveryRequestsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MfaRecoveryRequestSummary>> ListAccountMfaRecoveryHistoryAsync(
        Guid accountId,
        int limit,
        CancellationToken cancellationToken);

    Task<CompanyStatusGovernanceResult?> SetCompanyStatusAsync(
        Guid companyId,
        string status,
        string reason,
        Guid? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<AccountStatusGovernanceResult?> SetAccountStatusAsync(
        Guid accountId,
        string status,
        DateTimeOffset? lockedUntilUtc,
        string reason,
        Guid? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<PasswordResetGovernanceResult?> RequestPasswordResetAsync(
        Guid accountId,
        string reason,
        Guid? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<AccountMfaResetGovernanceResult?> ResetAccountMfaAsync(
        Guid accountId,
        string reason,
        Guid? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<MfaRecoveryReviewResult?> ReviewMfaRecoveryRequestAsync(
        Guid requestId,
        string decision,
        string reason,
        Guid? sysAdminAccountId,
        CancellationToken cancellationToken);

    Task<MfaRecoveryExecutionResult?> ExecuteMfaRecoveryRequestAsync(
        Guid requestId,
        string reason,
        Guid? sysAdminAccountId,
        CancellationToken cancellationToken);
}
