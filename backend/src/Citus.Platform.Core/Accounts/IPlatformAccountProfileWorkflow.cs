namespace Citus.Platform.Core.Accounts;

public interface IPlatformAccountProfileWorkflow
{
    Task<PlatformAccountProfileSummary?> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformMfaTimelineEntry>> GetMfaTimelineAsync(Guid userId, CancellationToken cancellationToken);

    Task<PlatformTotpEnrollmentStartResult?> BeginTotpEnrollmentAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<PlatformTotpEnrollmentConfirmationResult?> ConfirmTotpEnrollmentAsync(
        Guid userId,
        Guid enrollmentId,
        string verificationCode,
        CancellationToken cancellationToken);

    Task<PlatformAccountProfileSummary?> SaveDisplayNameAsync(
        Guid userId,
        string displayName,
        CancellationToken cancellationToken);

    Task<PlatformAccountProfileSummary?> SaveMfaModeAsync(
        Guid userId,
        string mfaMode,
        CancellationToken cancellationToken);

    Task<PlatformMfaRecoveryRequestResult?> RequestMfaRecoveryAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeRequestResult?> RequestEmailChangeAsync(
        Guid userId,
        string newEmail,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeRequestResult?> RequestPasswordChangeAsync(
        Guid userId,
        string newPassword,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeConfirmationResult?> ConfirmEmailChangeAsync(
        Guid userId,
        string verificationCode,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeConfirmationResult?> ConfirmPasswordChangeAsync(
        Guid userId,
        string verificationCode,
        CancellationToken cancellationToken);
}
