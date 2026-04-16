using Citus.Platform.Core.Accounts;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Xunit;

namespace Citus.Platform.Core.Tests;

public sealed class PlatformAccountProfileWorkflowTests
{
    private static readonly Guid UserId = Guid.Parse("f3fbb642-60c1-4a33-b14b-f633a95d7ee9");

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

        public string? ConfirmedEmailChangeCode { get; private set; }

        public Task<PlatformAccountProfileSummary?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<PlatformAccountProfileSummary?>(new PlatformAccountProfileSummary { UserId = userId });

        public Task<PlatformAccountProfileSummary?> SaveDisplayNameAsync(
            Guid userId,
            string displayName,
            CancellationToken cancellationToken)
        {
            SavedDisplayName = displayName;
            return Task.FromResult<PlatformAccountProfileSummary?>(new PlatformAccountProfileSummary { UserId = userId, DisplayName = displayName });
        }

        public Task<PlatformProfileChangeRequestResult?> RequestEmailChangeAsync(
            Guid userId,
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
            Guid userId,
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
            Guid userId,
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
            Guid userId,
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
