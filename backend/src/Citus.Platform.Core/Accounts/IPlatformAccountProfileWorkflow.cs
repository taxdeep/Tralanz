namespace Citus.Platform.Core.Accounts;

public interface IPlatformAccountProfileWorkflow
{
    Task<PlatformAccountProfileSummary?> GetAsync(UserId userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformMfaTimelineEntry>> GetMfaTimelineAsync(UserId userId, CancellationToken cancellationToken);

    Task<PlatformTotpEnrollmentStartResult?> BeginTotpEnrollmentAsync(
        UserId userId,
        CancellationToken cancellationToken);

    Task<PlatformTotpEnrollmentConfirmationResult?> ConfirmTotpEnrollmentAsync(
        UserId userId,
        Guid enrollmentId,
        string verificationCode,
        CancellationToken cancellationToken);

    Task<PlatformAccountProfileSummary?> SaveDisplayNameAsync(
        UserId userId,
        string displayName,
        CancellationToken cancellationToken);

    Task<PlatformAccountProfileSummary?> SaveMfaModeAsync(
        UserId userId,
        string mfaMode,
        CancellationToken cancellationToken);

    Task<PlatformMfaRecoveryRequestResult?> RequestMfaRecoveryAsync(
        UserId userId,
        string reason,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeRequestResult?> RequestEmailChangeAsync(
        UserId userId,
        string newEmail,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeRequestResult?> RequestPasswordChangeAsync(
        UserId userId,
        string newPassword,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeConfirmationResult?> ConfirmEmailChangeAsync(
        UserId userId,
        string verificationCode,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeConfirmationResult?> ConfirmPasswordChangeAsync(
        UserId userId,
        string verificationCode,
        CancellationToken cancellationToken);
}
