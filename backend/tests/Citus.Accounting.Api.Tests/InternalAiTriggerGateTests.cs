using Citus.Accounting.Api.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Citus.Accounting.Api.Tests;

/// <summary>
/// Verifies the /internal/ai/* gate, focusing on the tenant binding:
/// when UnityAi:ManualTriggerAllowedCompanyIds is set, the token may only act
/// on those companies; empty preserves prior (any-company) behavior.
/// </summary>
public sealed class InternalAiTriggerGateTests
{
    private static IConfiguration Config(string? token, string? allowList) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UnityAi:ManualTriggerBootstrapToken"] = token,
            ["UnityAi:ManualTriggerAllowedCompanyIds"] = allowList,
        }).Build();

    private static HttpContext Ctx(string? bearer)
    {
        var ctx = new DefaultHttpContext();
        if (bearer is not null)
        {
            ctx.Request.Headers["Authorization"] = bearer;
        }
        return ctx;
    }

    private static int? Status(IResult? result) => (result as IStatusCodeHttpResult)?.StatusCode;

    [Fact]
    public void Disabled_503_when_token_not_configured()
    {
        var r = InternalAiTriggerGate.Authorize(Ctx("Bearer x"), Config(null, null), CompanyId.FromOrdinal(1), "t", NullLogger.Instance);
        Assert.Equal(503, Status(r));
    }

    [Fact]
    public void Unauthorized_401_on_bad_token()
    {
        var r = InternalAiTriggerGate.Authorize(Ctx("Bearer wrong"), Config("secret", null), CompanyId.FromOrdinal(1), "t", NullLogger.Instance);
        Assert.Equal(401, Status(r));
    }

    [Fact]
    public void Authorized_any_company_when_allowlist_empty()
    {
        var r = InternalAiTriggerGate.Authorize(Ctx("Bearer secret"), Config("secret", ""), CompanyId.FromOrdinal(1), "t", NullLogger.Instance);
        Assert.Null(r); // null == proceed
    }

    [Fact]
    public void Authorized_when_company_in_allowlist()
    {
        var cid = CompanyId.FromOrdinal(1);
        var r = InternalAiTriggerGate.Authorize(Ctx("Bearer secret"), Config("secret", $"  {cid.Value} , some-other "), cid, "t", NullLogger.Instance);
        Assert.Null(r);
    }

    [Fact]
    public void Forbidden_403_when_company_not_in_allowlist()
    {
        var cid = CompanyId.FromOrdinal(1);
        var r = InternalAiTriggerGate.Authorize(Ctx("Bearer secret"), Config("secret", "some-other-company"), cid, "t", NullLogger.Instance);
        Assert.Equal(403, Status(r)); // tenant binding blocks
    }
}
