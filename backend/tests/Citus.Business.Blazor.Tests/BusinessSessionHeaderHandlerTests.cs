using System.Net;
using Citus.Business.Blazor.Configuration;
using Citus.Business.Blazor.Services;
using Citus.Business.Blazor.State;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Shell;

namespace Citus.Business.Blazor.Tests;

public sealed class BusinessSessionHeaderHandlerTests
{
    [Fact]
    public async Task SendAsync_AppendsBootstrapHeaders()
    {
        var state = CreateState();
        var capture = new CapturingHandler();
        using var invoker = new HttpMessageInvoker(new BusinessSessionHeaderHandler(state)
        {
            InnerHandler = capture
        });

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
            Id = Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6"),
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

        var capture = new CapturingHandler();
        using var invoker = new HttpMessageInvoker(new BusinessSessionHeaderHandler(state)
        {
            InnerHandler = capture
        });

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/accounting/session/context"), CancellationToken.None);

        Assert.NotNull(capture.Request);
        Assert.Equal(
            blueHarbor.Id.ToString(),
            capture.Request!.Headers.GetValues(BusinessSessionHeaderNames.ActiveCompanyId).Single());
    }

    private static BusinessShellState CreateState()
    {
        var bootstrap = new AppHostOptions();
        var state = new BusinessShellState();
        state.ApplyAuthenticatedSession(
            "bootstrap:test",
            new BusinessAuthSessionSummary
            {
                User = new BusinessUserSummary
                {
                    Id = bootstrap.BootstrapUserId,
                    DisplayName = bootstrap.BootstrapUserDisplayName,
                    Email = bootstrap.BootstrapUserEmail,
                    Username = bootstrap.BootstrapUsername,
                    Roles = bootstrap.BootstrapRoles
                },
                ActiveCompany = new BusinessCompanySummary
                {
                    Id = bootstrap.BootstrapCompanyId,
                    CompanyCode = bootstrap.BootstrapCompanyCode,
                    CompanyName = bootstrap.BootstrapCompanyName,
                    BaseCurrencyCode = bootstrap.BootstrapCompanyBaseCurrencyCode,
                    MultiCurrencyEnabled = bootstrap.BootstrapCompanyMultiCurrencyEnabled
                },
                AvailableCompanies = new List<BusinessCompanySummary>
                {
                    new()
                    {
                        Id = bootstrap.BootstrapCompanyId,
                        CompanyCode = bootstrap.BootstrapCompanyCode,
                        CompanyName = bootstrap.BootstrapCompanyName,
                        BaseCurrencyCode = bootstrap.BootstrapCompanyBaseCurrencyCode,
                        MultiCurrencyEnabled = bootstrap.BootstrapCompanyMultiCurrencyEnabled
                    }
                }
            },
            isBootstrap: true);
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
