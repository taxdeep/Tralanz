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
            UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d"),
            ActiveCompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc")
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
            UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d"),
            ActiveCompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc")
        };

        var result = guard.Validate(
        [
            new GuardProbeRequest
            {
                CompanyId = Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6"),
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
            UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d"),
            ActiveCompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc")
        };

        var result = guard.Validate(
        [
            new GuardProbeRequest
            {
                CompanyId = session.ActiveCompanyId,
                UserId = Guid.Parse("64f5186b-b854-49ec-a473-2f14554ecf77")
            }
        ],
        session);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("does not match the authenticated business session", result.Message, StringComparison.Ordinal);
    }

    public sealed class GuardProbeRequest
    {
        public Guid CompanyId { get; init; }

        public Guid UserId { get; init; }
    }
}
