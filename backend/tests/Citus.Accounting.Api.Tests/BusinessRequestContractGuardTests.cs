using Microsoft.AspNetCore.Http;

namespace Citus.Accounting.Api.Tests;

public sealed class BusinessRequestContractGuardTests
{
    [Fact]
    public void Validate_AllowsMatchingContract()
    {
        var guard = new BusinessRequestContractGuard();
        var session = new BusinessSessionContext
        {
            UserId = UserId.FromOrdinal(1),
            ActiveCompanyId = CompanyId.FromOrdinal(1)
        };

        var result = guard.Validate(
        [
            new GuardProbeRequest
            {
                CompanyId = session.ActiveCompanyId,
                UserId = session.UserId
            }
        ],
        session);

        Assert.True(result.Allowed);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    [Fact]
    public void Validate_RejectsCompanyMismatch()
    {
        var guard = new BusinessRequestContractGuard();
        var session = new BusinessSessionContext
        {
            UserId = UserId.FromOrdinal(1),
            ActiveCompanyId = CompanyId.FromOrdinal(1)
        };

        var result = guard.Validate(
        [
            new GuardProbeRequest
            {
                CompanyId = CompanyId.FromOrdinal(2),
                UserId = session.UserId
            }
        ],
        session);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("does not match the active company context", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsUserMismatch()
    {
        var guard = new BusinessRequestContractGuard();
        var session = new BusinessSessionContext
        {
            UserId = UserId.FromOrdinal(1),
            ActiveCompanyId = CompanyId.FromOrdinal(1)
        };

        var result = guard.Validate(
        [
            new GuardProbeRequest
            {
                CompanyId = session.ActiveCompanyId,
                UserId = UserId.FromOrdinal(2)
            }
        ],
        session);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("does not match the authenticated business session", result.Message, StringComparison.Ordinal);
    }

    public sealed class GuardProbeRequest
    {
        public CompanyId CompanyId { get; init; }

        public UserId UserId { get; init; }
    }
}
