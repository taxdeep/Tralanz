using Citus.Platform.Core.Accounts;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Xunit;

namespace Citus.Platform.Core.Tests;

public sealed class PlatformAccountProfileWorkflowTests
{
    private static readonly UserId UserId = UserId.FromOrdinal(1);

    [Fact]
    public async Task SaveDisplayNameTrimsBeforePersisting()
    {
        var repository = new InMemoryPlatformAccountProfileRepository();
        var workflow = new PlatformAccountProfileWorkflow(repository, new SysAdminPasswordHasher());

        await workflow.SaveDisplayNameAsync(UserId, "  Alice Rowan  ", CancellationToken.None);

        Assert.Equal("Alice Rowan", repository.SavedDisplayName);
    }

    [Fact]
    public async Task RequestEmailChangeNormalizesEmailBeforePersisting()
    {
        var repository = new InMemoryPlatformAccountProfileRepository();
        var workflow = new PlatformAccountProfileWorkflow(repository, new SysAdminPasswordHasher());

        await workflow.RequestEmailChangeAsync(UserId, "  Alice.Rowan@Example.COM  ", CancellationToken.None);

        Assert.Equal("alice.rowan@example.com", repository.RequestedEmail);
    }

    [Fact]
    public async Task RequestPasswordChangeHashesBeforePersisting()
    {
        var repository = new InMemoryPlatformAccountProfileRepository();
        var passwordHasher = new SysAdminPasswordHasher();
        var workflow = new PlatformAccountProfileWorkflow(repository, passwordHasher);

        await workflow.RequestPasswordChangeAsync(UserId, "change-me-please", CancellationToken.None);

        Assert.NotNull(repository.RequestedPasswordHash);
        Assert.NotEqual("change-me-please", repository.RequestedPasswordHash);
        Assert.True(passwordHasher.VerifyPassword("change-me-please", repository.RequestedPasswordHash!));
    }

    [Fact]
    public async Task SaveMfaModeNormalizesBeforePersisting()
    {
        var repository = new InMemoryPlatformAccountProfileRepository();
        var workflow = new PlatformAccountProfileWorkflow(repository, new SysAdminPasswordHasher());

        await workflow.SaveMfaModeAsync(UserId, " Email_Code ", CancellationToken.None);

        Assert.Equal("email_code", repository.SavedMfaMode);
    }

    [Fact]
    public async Task SaveMfaMode_ExplainsThatTotpEnrollmentIsNotAvailableYet()
    {
        var repository = new InMemoryPlatformAccountProfileRepository();
        var workflow = new PlatformAccountProfileWorkflow(repository, new SysAdminPasswordHasher());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SaveMfaModeAsync(UserId, "totp_app", CancellationToken.None));

        Assert.Equal(
            "Use the TOTP enrollment flow before enabling authenticator-app MFA.",
            error.Message);
    }

    [Fact]
    public async Task ConfirmTotpEnrollment_NormalizesVerificationCode()
    {
        var repository = new InMemoryPlatformAccountProfileRepository();
        var workflow = new PlatformAccountProfileWorkflow(repository, new SysAdminPasswordHasher());
        var enrollmentId = Guid.Parse("88d3d934-db47-4ec5-9af2-37db3ad81a5c");

        await workflow.ConfirmTotpEnrollmentAsync(UserId, enrollmentId, " 123456 ", CancellationToken.None);

        Assert.Equal(enrollmentId, repository.ConfirmedTotpEnrollmentId);
        Assert.Equal("123456", repository.ConfirmedTotpVerificationCode);
    }

    [Fact]
    public async Task RequestMfaRecoveryNormalizesReasonBeforePersisting()
    {
        var repository = new InMemoryPlatformAccountProfileRepository();
        var workflow = new PlatformAccountProfileWorkflow(repository, new SysAdminPasswordHasher());

        await workflow.RequestMfaRecoveryAsync(UserId, "  Lost access to the mailbox device used for MFA.  ", CancellationToken.None);

        Assert.Equal("Lost access to the mailbox device used for MFA.", repository.RequestedMfaRecoveryReason);
    }

    [Fact]
    public async Task ConfirmEmailChangeNormalizesVerificationCode()
    {
        var repository = new InMemoryPlatformAccountProfileRepository();
        var workflow = new PlatformAccountProfileWorkflow(repository, new SysAdminPasswordHasher());

        await workflow.ConfirmEmailChangeAsync(UserId, " ab12cd ", CancellationToken.None);

        Assert.Equal("AB12CD", repository.ConfirmedEmailChangeCode);
    }

    private sealed class InMemoryPlatformAccountProfileRepository : IPlatformAccountProfileRepository
    {
        public string? SavedDisplayName { get; private set; }

        public string? RequestedEmail { get; private set; }

        public string? RequestedPasswordHash { get; private set; }

        public string? SavedMfaMode { get; private set; }

        public string? RequestedMfaRecoveryReason { get; private set; }

        public string? ConfirmedEmailChangeCode { get; private set; }

        public Guid? ConfirmedTotpEnrollmentId { get; private set; }

        public string? ConfirmedTotpVerificationCode { get; private set; }

        public Task<PlatformAccountProfileSummary?> GetAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<PlatformAccountProfileSummary?>(new PlatformAccountProfileSummary { UserId = userId });

        public Task<IReadOnlyList<PlatformMfaTimelineEntry>> GetMfaTimelineAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlatformMfaTimelineEntry>>(Array.Empty<PlatformMfaTimelineEntry>());

        public Task<PlatformTotpEnrollmentStartResult?> BeginTotpEnrollmentAsync(
            UserId userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<PlatformTotpEnrollmentStartResult?>(new PlatformTotpEnrollmentStartResult
            {
                EnrollmentId = Guid.NewGuid(),
                Issuer = "Citus",
                AccountLabel = "alice@example.com",
                SecretBase32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567",
                OtpAuthUri = "otpauth://totp/Citus:alice@example.com",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
                Profile = new PlatformAccountProfileSummary { UserId = userId }
            });

        public Task<PlatformTotpEnrollmentConfirmationResult?> ConfirmTotpEnrollmentAsync(
            UserId userId,
            Guid enrollmentId,
            string verificationCode,
            CancellationToken cancellationToken)
        {
            ConfirmedTotpEnrollmentId = enrollmentId;
            ConfirmedTotpVerificationCode = verificationCode;
            return Task.FromResult<PlatformTotpEnrollmentConfirmationResult?>(new PlatformTotpEnrollmentConfirmationResult
            {
                EnrollmentId = enrollmentId,
                ConfirmedAtUtc = DateTimeOffset.UtcNow,
                Profile = new PlatformAccountProfileSummary { UserId = userId, MfaMode = "totp_app" }
            });
        }

        public Task<PlatformAccountProfileSummary?> SaveDisplayNameAsync(
            UserId userId,
            string displayName,
            CancellationToken cancellationToken)
        {
            SavedDisplayName = displayName;
            return Task.FromResult<PlatformAccountProfileSummary?>(new PlatformAccountProfileSummary { UserId = userId, DisplayName = displayName });
        }

        public Task<PlatformAccountProfileSummary?> SaveMfaModeAsync(
            UserId userId,
            string mfaMode,
            CancellationToken cancellationToken)
        {
            SavedMfaMode = mfaMode;
            return Task.FromResult<PlatformAccountProfileSummary?>(new PlatformAccountProfileSummary { UserId = userId, MfaMode = mfaMode });
        }

        public Task<PlatformMfaRecoveryRequestResult?> RequestMfaRecoveryAsync(
            UserId userId,
            string reason,
            CancellationToken cancellationToken)
        {
            RequestedMfaRecoveryReason = reason;
            return Task.FromResult<PlatformMfaRecoveryRequestResult?>(new PlatformMfaRecoveryRequestResult
            {
                RequestId = Guid.NewGuid(),
                Status = "requested",
                Reason = reason,
                RequestedAtUtc = DateTimeOffset.UtcNow,
                Profile = new PlatformAccountProfileSummary { UserId = userId, MfaMode = "email_code" }
            });
        }

        public Task<PlatformProfileChangeRequestResult?> RequestEmailChangeAsync(
            UserId userId,
            string newEmail,
            CancellationToken cancellationToken)
        {
            RequestedEmail = newEmail;
            return Task.FromResult<PlatformProfileChangeRequestResult?>(new PlatformProfileChangeRequestResult
            {
                ChangeType = "email_change",
                MaskedDestination = "a***@example.com",
                ExpiresAtUtc = DateTimeOffset.UtcNow,
                Profile = new PlatformAccountProfileSummary { UserId = userId }
            });
        }

        public Task<PlatformProfileChangeRequestResult?> RequestPasswordChangeAsync(
            UserId userId,
            string newPasswordHash,
            CancellationToken cancellationToken)
        {
            RequestedPasswordHash = newPasswordHash;
            return Task.FromResult<PlatformProfileChangeRequestResult?>(new PlatformProfileChangeRequestResult
            {
                ChangeType = "password_change",
                MaskedDestination = "a***@example.com",
                ExpiresAtUtc = DateTimeOffset.UtcNow,
                Profile = new PlatformAccountProfileSummary { UserId = userId }
            });
        }

        public Task<PlatformProfileChangeConfirmationResult?> ConfirmEmailChangeAsync(
            UserId userId,
            string verificationCode,
            CancellationToken cancellationToken)
        {
            ConfirmedEmailChangeCode = verificationCode;
            return Task.FromResult<PlatformProfileChangeConfirmationResult?>(new PlatformProfileChangeConfirmationResult
            {
                ChangeType = "email_change",
                ConfirmedAtUtc = DateTimeOffset.UtcNow,
                Profile = new PlatformAccountProfileSummary { UserId = userId }
            });
        }

        public Task<PlatformProfileChangeConfirmationResult?> ConfirmPasswordChangeAsync(
            UserId userId,
            string verificationCode,
            CancellationToken cancellationToken) =>
            Task.FromResult<PlatformProfileChangeConfirmationResult?>(new PlatformProfileChangeConfirmationResult
            {
                ChangeType = "password_change",
                ConfirmedAtUtc = DateTimeOffset.UtcNow,
                Profile = new PlatformAccountProfileSummary { UserId = userId }
            });
    }
}
