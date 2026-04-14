using Citus.Platform.Core.Runtime;
using Microsoft.AspNetCore.Http;

namespace Citus.Accounting.Api.Tests;

public sealed class BusinessRouteGuardTests
{
    private static readonly Guid UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");
    private static readonly Guid CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");

    [Fact]
    public void Evaluate_BlocksWriteRequests_WhenMaintenanceModeIsEnabled()
    {
        var guard = CreateGuard();

        var result = guard.Evaluate(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            Array.Empty<object?>(),
            new PlatformMaintenanceState
            {
                Enabled = true,
                Message = "Maintenance window in progress."
            });

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Maintenance window in progress.", result.Message);
    }

    [Fact]
    public void Evaluate_RequiresSessionHeaders()
    {
        var guard = CreateGuard();

        var result = guard.Evaluate(
            HttpMethods.Get,
            new HeaderDictionary(),
            Array.Empty<object?>(),
            maintenanceState: null);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Contains(BusinessSessionHeaders.UserId, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RejectsUserCompanyMembershipMismatch()
    {
        var guard = CreateGuard();

        var result = guard.Evaluate(
            HttpMethods.Get,
            CreateHeaders(UserId, Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6")),
            Array.Empty<object?>(),
            maintenanceState: null);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Contains("does not belong to company", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AssignsResolvedSession_WhenRequestIsValid()
    {
        var guard = CreateGuard();

        var result = guard.Evaluate(
            HttpMethods.Get,
            CreateHeaders(UserId, CompanyId),
            [
                new GuardProbeRequest
                {
                    CompanyId = CompanyId,
                    UserId = UserId
                }
            ],
            maintenanceState: null);

        Assert.True(result.Allowed);
        Assert.NotNull(result.Session);
        Assert.Equal(UserId, result.Session.UserId);
        Assert.Equal(CompanyId, result.Session.ActiveCompanyId);
    }

    private static BusinessRouteGuard CreateGuard() =>
        new(
            new BusinessSessionRequestReader(),
            new BusinessRequestContractGuard(),
            new BusinessSessionDirectory(Microsoft.Extensions.Options.Options.Create(new BusinessSessionOptions())));

    private static HeaderDictionary CreateHeaders(Guid userId, Guid companyId) =>
        new()
        {
            [BusinessSessionHeaders.UserId] = userId.ToString(),
            [BusinessSessionHeaders.ActiveCompanyId] = companyId.ToString()
        };

    public sealed class GuardProbeRequest
    {
        public Guid CompanyId { get; init; }

        public Guid UserId { get; init; }
    }
}
