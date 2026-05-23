using Citus.Accounting.Api;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Citus.Ui.Shared.Control;
using Microsoft.AspNetCore.Http;

namespace Citus.Accounting.Api.Tests;

public sealed class SysAdminControlPlaneGateTests
{
    [Fact]
    public async Task ValidateAsync_BlocksMissingSysAdminSessionHeader()
    {
        var repository = new FakeSysAdminAuthRepository();

        var result = await SysAdminControlPlaneGate.ValidateAsync(
            new DefaultHttpContext(),
            repository,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Equal("missing_sysadmin_session", result.OutcomeCode);
        Assert.Null(result.SysAdminAccountId);
        Assert.False(repository.ValidateCalled);
    }

    [Fact]
    public async Task ValidateAsync_BlocksInvalidSysAdminSession()
    {
        var repository = new FakeSysAdminAuthRepository
        {
            ValidationResult = new SysAdminSessionValidationResult
            {
                Succeeded = false,
                FailureCode = "expired_session",
                FailureMessage = "SysAdmin session has expired."
            }
        };
        var context = CreateHttpContext("expired-token");

        var result = await SysAdminControlPlaneGate.ValidateAsync(
            context,
            repository,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Equal("expired_session", result.OutcomeCode);
        Assert.Null(result.SysAdminAccountId);
        Assert.True(repository.ValidateCalled);
        Assert.Equal("expired-token", repository.LastSessionToken);
    }

    [Fact]
    public async Task ValidateAsync_AllowsValidSysAdminSession()
    {
        var sysAdminAccountId = UserId.FromOrdinal(7);
        var repository = new FakeSysAdminAuthRepository
        {
            ValidationResult = new SysAdminSessionValidationResult
            {
                Succeeded = true,
                SysAdminAccountId = sysAdminAccountId,
                Email = "sysadmin@example.test",
                Roles = ["sysadmin"],
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            }
        };
        var context = CreateHttpContext("valid-token");

        var result = await SysAdminControlPlaneGate.ValidateAsync(
            context,
            repository,
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("sysadmin_session_allowed", result.OutcomeCode);
        Assert.Equal(sysAdminAccountId, result.SysAdminAccountId);
        Assert.Null(result.Response);
        Assert.Equal("valid-token", repository.LastSessionToken);
    }

    private static DefaultHttpContext CreateHttpContext(string sessionToken)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[SysAdminAuthConstants.SessionHeaderName] = sessionToken;
        return context;
    }

    private sealed class FakeSysAdminAuthRepository : ISysAdminAuthRepository
    {
        public bool ValidateCalled { get; private set; }

        public string? LastSessionToken { get; private set; }

        public SysAdminSessionValidationResult ValidationResult { get; init; } = new()
        {
            Succeeded = true,
            SysAdminAccountId = UserId.FromOrdinal(1)
        };

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SysAdminSetupStatus> GetSetupStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task EnsureBootstrapAccountAsync(
            string email,
            string password,
            string displayName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SysAdminFirstAccountProvisioningResult> ProvisionFirstAccountAsync(
            string email,
            string password,
            string displayName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SysAdminAuthenticationResult> AuthenticateAsync(
            string email,
            string password,
            TimeSpan sessionLifetime,
            string? remoteIp,
            string? userAgent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SysAdminSessionValidationResult> ValidateSessionAsync(
            string sessionToken,
            CancellationToken cancellationToken)
        {
            ValidateCalled = true;
            LastSessionToken = sessionToken;
            return Task.FromResult(ValidationResult);
        }

        public Task RevokeSessionAsync(string sessionToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SysAdminSecretRotationResult> RotateSecretAsync(
            UserId sysAdminAccountId,
            string currentPassword,
            string newPassword,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
