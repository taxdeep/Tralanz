using System.Net;
using Citus.Business.Blazor.Configuration;
using Citus.Business.Blazor.Services;
using Citus.Business.Blazor.State;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Citus.Business.Blazor.Tests;

public sealed class BusinessSessionHeaderHandlerTests
{
    [Fact]
    public async Task SendAsync_AppendsBootstrapHeaders()
    {
        var state = CreateState();
        var (handler, capture) = BuildHandler(state);
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/health"), CancellationToken.None);

        Assert.NotNull(capture.Request);
        Assert.Equal(
            state.CurrentUserId.ToString(),
            capture.Request!.Headers.GetValues(BusinessSessionHeaderNames.UserId).Single());
        Assert.Equal(
            state.ActiveCompany.Id.ToString(),
            capture.Request.Headers.GetValues(BusinessSessionHeaderNames.ActiveCompanyId).Single());
    }

    [Fact]
    public async Task SendAsync_UsesUpdatedActiveCompany()
    {
        var state = CreateState();
        var northwind = state.ActiveCompany;
        var blueHarbor = new BusinessCompanySummary
        {
            Id = CompanyId.FromOrdinal(2),
            CompanyCode = "BLUEHARBOR",
            CompanyName = "Blue Harbor Trading Co.",
            BaseCurrencyCode = "CAD",
            MultiCurrencyEnabled = false
        };

        state.ApplySessionContext(new BusinessSessionContextSummary
        {
            User = state.User,
            ActiveCompany = northwind,
            AvailableCompanies = [northwind, blueHarbor],
            MaintenanceState = new MaintenanceStateSummary()
        });

        Assert.True(state.TrySetActiveCompany(blueHarbor.Id));

        var (handler, capture) = BuildHandler(state);
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/accounting/session/context"), CancellationToken.None);

        Assert.NotNull(capture.Request);
        Assert.Equal(
            blueHarbor.Id.ToString(),
            capture.Request!.Headers.GetValues(BusinessSessionHeaderNames.ActiveCompanyId).Single());
    }

    [Fact]
    public async Task SendAsync_OmitsHeaders_WhenNoCircuitScopeIsActive()
    {
        // Reproduces the production captive-dependency scenario: handler is
        // constructed and invoked without a Blazor circuit ever setting
        // CircuitServicesAccessor.Services. Headers must be omitted entirely
        // (not sent as Guid.Empty), so the receiving guard responds with a
        // clean 401 "missing required header" instead of a misleading
        // "no membership for company 00000000-..." rejection.
        var accessor = new CircuitServicesAccessor();
        var capture = new CapturingHandler();
        var handler = new BusinessSessionHeaderHandler(accessor) { InnerHandler = capture };
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/anything"), CancellationToken.None);

        Assert.NotNull(capture.Request);
        Assert.False(capture.Request!.Headers.Contains(BusinessSessionHeaderNames.UserId));
        Assert.False(capture.Request.Headers.Contains(BusinessSessionHeaderNames.ActiveCompanyId));
    }

    private static (BusinessSessionHeaderHandler Handler, CapturingHandler Capture) BuildHandler(BusinessShellState state)
    {
        // Fake the circuit's IServiceProvider with a tiny container holding
        // just the state we need; mirrors what CircuitServicesAccessorCircuitHandler
        // does at runtime when a real Blazor circuit is active.
        var services = new ServiceCollection();
        services.AddSingleton(state);
        var provider = services.BuildServiceProvider();
        var accessor = new CircuitServicesAccessor { Services = provider };

        var capture = new CapturingHandler();
        var handler = new BusinessSessionHeaderHandler(accessor) { InnerHandler = capture };
        return (handler, capture);
    }

    private static BusinessShellState CreateState()
    {
        // Test-local identity. The handler under test only inspects the
        // active user / company ids, so any non-empty fixture works.
        var userId = UserId.FromOrdinal(1);
        var companyId = CompanyId.FromOrdinal(1);

        var state = new BusinessShellState();
        state.ApplyAuthenticatedSession(
            "session-test",
            new BusinessAuthSessionSummary
            {
                User = new BusinessUserSummary
                {
                    Id = userId,
                    DisplayName = "Alice Rowan",
                    Email = "alice.rowan@northwind.example",
                    Username = "alice.rowan",
                    Roles = ["owner", "reports"]
                },
                ActiveCompany = new BusinessCompanySummary
                {
                    Id = companyId,
                    CompanyCode = "NORTHWIND",
                    CompanyName = "Northwind Studio Ltd.",
                    BaseCurrencyCode = "USD",
                    MultiCurrencyEnabled = true
                },
                AvailableCompanies = new List<BusinessCompanySummary>
                {
                    new()
                    {
                        Id = companyId,
                        CompanyCode = "NORTHWIND",
                        CompanyName = "Northwind Studio Ltd.",
                        BaseCurrencyCode = "USD",
                        MultiCurrencyEnabled = true
                    }
                }
            });
        return state;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
