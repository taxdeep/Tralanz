using Citus.Platform.Core.Accounts;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformAccountProfileRepository
{
    Task<PlatformAccountProfileSummary?> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<PlatformAccountProfileSummary?> SaveDisplayNameAsync(
        Guid userId,
        string displayName,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeRequestResult?> RequestEmailChangeAsync(
        Guid userId,
        string newEmail,
        CancellationToken cancellationToken);

    Task<PlatformProfileChangeRequestResult?> RequestPasswordChangeAsync(
        Guid userId,
        string newPasswordHash,
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
