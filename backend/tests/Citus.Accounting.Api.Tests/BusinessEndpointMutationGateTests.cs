using Microsoft.AspNetCore.Http;

namespace Citus.Accounting.Api.Tests;

public sealed class BusinessEndpointMutationGateTests
{
    [Fact]
    public void ValidateCompanyScopedMutation_BlocksMissingSession()
    {
        var result = BusinessEndpointMutationGate.ValidateCompanyScopedMutation(
            session: null,
            CompanyId.FromOrdinal(1),
            "accounting",
            "reverse source documents");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Equal("business_session_required", result.OutcomeCode);
        Assert.NotNull(result.Response);
    }

    [Fact]
    public void ValidateCompanyScopedMutation_BlocksCrossCompanyMutation()
    {
        var result = BusinessEndpointMutationGate.ValidateCompanyScopedMutation(
            CreateSession(CompanyId.FromOrdinal(1), UserId.FromOrdinal(7), "company_book_governance"),
            CompanyId.FromOrdinal(2),
            "accounting",
            "reverse source documents");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("active_company_mismatch", result.OutcomeCode);
        Assert.NotNull(result.Response);
    }

    [Fact]
    public void ValidateCompanyScopedMutation_BlocksMissingActor()
    {
        var result = BusinessEndpointMutationGate.ValidateCompanyScopedMutation(
            CreateSession(CompanyId.FromOrdinal(1), default, "company_book_governance"),
            CompanyId.FromOrdinal(1),
            "accounting",
            "void source documents");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Equal("business_actor_required", result.OutcomeCode);
        Assert.NotNull(result.Response);
    }

    [Fact]
    public void ValidateCompanyScopedMutation_BlocksUserWithoutModuleAuthority()
    {
        var result = BusinessEndpointMutationGate.ValidateCompanyScopedMutation(
            CreateSession(CompanyId.FromOrdinal(1), UserId.FromOrdinal(7), "sales"),
            CompanyId.FromOrdinal(1),
            "accounting",
            "execute source document reverse requests");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("blocked_business_operation_authority", result.OutcomeCode);
        Assert.NotNull(result.Response);
    }

    [Fact]
    public void ValidateCompanyScopedMutation_AllowsAccountingGovernanceAndReturnsActor()
    {
        var actorId = UserId.FromOrdinal(7);

        var result = BusinessEndpointMutationGate.ValidateCompanyScopedMutation(
            CreateSession(CompanyId.FromOrdinal(1), actorId, "company_book_governance"),
            CompanyId.FromOrdinal(1),
            "accounting",
            "execute source document reverse requests");

        Assert.True(result.Allowed);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("company_scoped_mutation_allowed", result.OutcomeCode);
        Assert.Equal(actorId, result.ActorId);
        Assert.Null(result.Response);
    }

    private static BusinessSessionContext CreateSession(CompanyId companyId, UserId userId, params string[] roles) =>
        new()
        {
            UserId = userId,
            ActiveCompanyId = companyId,
            Roles = roles
        };
}
