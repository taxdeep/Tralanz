using System.Net;
using System.Net.Http.Json;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Platform.Core.Runtime;
using Citus.Ui.Shared.Business;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.CompanyAccess.SessionContext;
using SharedKernel.CompanyAccess;

namespace Citus.Business.Blazor.Tests;

public sealed class BusinessSessionApiContractTests
{
    [Fact]
    public async Task SignIn_ReturnsSessionTokenAndResolvedContext_WhenCredentialsAndMembershipAreValid()
    {
        using var factory = new BusinessSessionApiApplicationFactory();
        var userId = Guid.Parse("d870565e-a630-477e-b915-aac95d706c1c");
        var companyId = Guid.Parse("078ec959-7c79-4542-bd09-2efc44b0c520");
        factory.BusinessSessions.AuthenticateResult = new PlatformBusinessSessionResult
        {
            Succeeded = true,
            SessionToken = "BUSINESS-TOKEN-01",
            UserId = userId,
            ActiveCompanyId = companyId,
            ExpiresAtUtc = new DateTimeOffset(2026, 4, 17, 4, 0, 0, TimeSpan.Zero)
        };
        factory.CompanyContext.ContextFactory = (_, preferredCompanyId, _) =>
            Task.FromResult<CompanyAccessSessionContext?>(CreateContext(userId, preferredCompanyId ?? companyId));

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/business/session/sign-in",
            new
            {
                login = "morgan@example.com",
                password = "Sup3rSecret!"
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<Web.Shell.Services.WebShellBusinessSignInResponse>();

        Assert.NotNull(payload);
        Assert.NotNull(payload!.Context);
        Assert.Equal("morgan@example.com", factory.BusinessSessions.LastLogin);
        Assert.Equal("Sup3rSecret!", factory.BusinessSessions.LastPassword);
        Assert.Equal("BUSINESS-TOKEN-01", payload!.SessionToken);
        Assert.Equal("authenticated", payload.AuthenticationStage);
        Assert.False(payload.RequiresSecondFactor);
        Assert.Null(payload.MfaChallengeId);
        Assert.Equal(companyId, payload.Context!.ActiveCompany.Id);
        Assert.Equal("Northwind Studio Ltd.", payload.Context.ActiveCompany.CompanyName);
    }

    [Fact]
    public async Task SignIn_ReturnsLocked_WhenMaintenanceIsEnabled()
    {
        using var factory = new BusinessSessionApiApplicationFactory();
        factory.RuntimeState.MaintenanceState = new PlatformMaintenanceState
        {
            Enabled = true,
            Message = "Maintenance mode is on."
        };

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/business/session/sign-in",
            new
            {
                login = "morgan@example.com",
                password = "Sup3rSecret!"
            });

        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        Assert.Null(factory.BusinessSessions.LastLogin);
    }

    [Fact]
    public async Task GetSession_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new BusinessSessionApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/business/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetContext_ReturnsResolvedContext_WhenSessionHeaderIsValid()
    {
        using var factory = new BusinessSessionApiApplicationFactory();
        var userId = Guid.Parse("a9d31d23-4e11-4208-9ca8-b5072c309098");
        var companyId = Guid.Parse("c7f59662-456c-4f4d-a572-16571ce7f55a");
        factory.BusinessSessions.ValidateResult = new PlatformBusinessSessionResult
        {
            Succeeded = true,
            UserId = userId,
            ActiveCompanyId = companyId,
            ExpiresAtUtc = new DateTimeOffset(2026, 4, 17, 6, 0, 0, TimeSpan.Zero)
        };
        factory.CompanyContext.ContextFactory = (_, preferredCompanyId, _) =>
            Task.FromResult<CompanyAccessSessionContext?>(CreateContext(userId, preferredCompanyId ?? companyId));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-02");

        var response = await client.GetAsync("/api/business/session/context");

        response.EnsureSuccessStatusCode();

        var context = await response.Content.ReadFromJsonAsync<BusinessSessionContextSummary>();

        Assert.NotNull(context);
        Assert.Equal("BUSINESS-TOKEN-02", factory.BusinessSessions.LastValidatedSessionToken);
        Assert.Equal(companyId, context!.ActiveCompany.Id);
    }

    [Fact]
    public async Task SwitchActiveCompany_ReturnsUpdatedContext_WhenAuthenticated()
    {
        using var factory = new BusinessSessionApiApplicationFactory();
        var userId = Guid.Parse("805a86c0-61f0-46cf-8362-ea290c0ca157");
        var switchedCompanyId = Guid.Parse("d0e6e221-d34f-4d28-98a2-5fd5259715b5");
        factory.BusinessSessions.SwitchResult = new PlatformBusinessSessionResult
        {
            Succeeded = true,
            UserId = userId,
            ActiveCompanyId = switchedCompanyId,
            ExpiresAtUtc = new DateTimeOffset(2026, 4, 17, 7, 0, 0, TimeSpan.Zero)
        };
        factory.CompanyContext.ContextFactory = (_, preferredCompanyId, _) =>
            Task.FromResult<CompanyAccessSessionContext?>(CreateContext(userId, preferredCompanyId ?? switchedCompanyId));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-03");

        var response = await client.PutAsJsonAsync(
            "/api/business/session/active-company",
            new
            {
                companyId = switchedCompanyId
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<Web.Shell.Services.WebShellBusinessSessionStateResponse>();

        Assert.NotNull(payload);
        Assert.Equal("BUSINESS-TOKEN-03", factory.BusinessSessions.LastSwitchedSessionToken);
        Assert.Equal(switchedCompanyId, factory.BusinessSessions.LastSwitchedCompanyId);
        Assert.Equal(switchedCompanyId, payload!.Context.ActiveCompany.Id);
    }

    [Fact]
    public async Task SignOut_RevokesCurrentSession()
    {
        using var factory = new BusinessSessionApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-04");

        var response = await client.PostAsync("/api/business/session/sign-out", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("BUSINESS-TOKEN-04", factory.BusinessSessions.LastRevokedSessionToken);
    }

    private static CompanyAccessSessionContext CreateContext(Guid userId, Guid activeCompanyId)
    {
        var activeCompany = new CompanyAccessCompanySummary
        {
            Id = activeCompanyId,
            CompanyCode = "NORTHWIND",
            CompanyName = "Northwind Studio Ltd.",
            BaseCurrencyCode = "USD",
            MultiCurrencyEnabled = true,
            Status = "active",
            IsReadOnly = false
        };
        var secondaryCompany = new CompanyAccessCompanySummary
        {
            Id = Guid.Parse("55ca7abf-39b5-43dd-bb8e-cfd68af33d9b"),
            CompanyCode = "BLUEHARBOR",
            CompanyName = "Blue Harbor Trading Co.",
            BaseCurrencyCode = "CAD",
            MultiCurrencyEnabled = false,
            Status = "inactive",
            IsReadOnly = true
        };

        return new CompanyAccessSessionContext
        {
            User = new CompanyAccessUserSummary
            {
                Id = userId,
                DisplayName = "Morgan Hale",
                Email = "morgan@example.com",
                Username = "morgan.hale",
                Roles = ["owner", "reports"]
            },
            ActiveCompany = activeCompany,
            AvailableCompanies = [activeCompany, secondaryCompany]
        };
    }

    private sealed class BusinessSessionApiApplicationFactory : WebApplicationFactory<global::Web.Shell.App>
    {
        public FakePlatformBusinessSessionRepository BusinessSessions { get; } = new();

        public FakeCompanySessionContextWorkflow CompanyContext { get; } = new();

        public FakePlatformRuntimeStateRepository RuntimeState { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(
                (_, configurationBuilder) =>
                {
                    configurationBuilder.AddInMemoryCollection(
                    [
                        KeyValuePair.Create<string, string?>("ConnectionStrings:AccountingCore", "Host=127.0.0.1;Port=5432;Database=citus_tests;Username=postgres;Password=postgres"),
                        KeyValuePair.Create<string, string?>("AppHost:DisableRazorComponents", bool.TrueString)
                    ]);
                });
            builder.ConfigureServices(
                services =>
                {
                    services.RemoveAll<IPlatformBusinessSessionRepository>();
                    services.AddSingleton<IPlatformBusinessSessionRepository>(BusinessSessions);
                    services.RemoveAll<ICompanySessionContextWorkflow>();
                    services.AddSingleton<ICompanySessionContextWorkflow>(CompanyContext);
                    services.RemoveAll<IPlatformRuntimeStateRepository>();
                    services.AddSingleton<IPlatformRuntimeStateRepository>(RuntimeState);
                });
        }
    }

    private sealed class FakePlatformBusinessSessionRepository : IPlatformBusinessSessionRepository
    {
        public PlatformBusinessSessionResult AuthenticateResult { get; set; } = new();

        public PlatformBusinessSessionResult ValidateResult { get; set; } = new();

        public PlatformBusinessSessionResult SwitchResult { get; set; } = new();

        public string? LastLogin { get; private set; }

        public string? LastPassword { get; private set; }

        public string? LastValidatedSessionToken { get; private set; }

        public string? LastSwitchedSessionToken { get; private set; }

        public Guid? LastSwitchedCompanyId { get; private set; }

        public string? LastRevokedSessionToken { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlatformBusinessSessionResult> AuthenticateAsync(
            string login,
            string password,
            TimeSpan sessionLifetime,
            string? remoteIp,
            string? userAgent,
            CancellationToken cancellationToken)
        {
            LastLogin = login;
            LastPassword = password;
            return Task.FromResult(AuthenticateResult);
        }

        public Task<PlatformBusinessSessionResult> ValidateSessionAsync(
            string sessionToken,
            CancellationToken cancellationToken)
        {
            LastValidatedSessionToken = sessionToken;
            return Task.FromResult(ValidateResult);
        }

        public Task<PlatformBusinessSessionResult> SwitchActiveCompanyAsync(
            string sessionToken,
            Guid activeCompanyId,
            CancellationToken cancellationToken)
        {
            LastSwitchedSessionToken = sessionToken;
            LastSwitchedCompanyId = activeCompanyId;
            return Task.FromResult(SwitchResult);
        }

        public Task RevokeSessionAsync(string sessionToken, CancellationToken cancellationToken)
        {
            LastRevokedSessionToken = sessionToken;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCompanySessionContextWorkflow : ICompanySessionContextWorkflow
    {
        public Func<Guid, Guid?, CancellationToken, Task<CompanyAccessSessionContext?>> ContextFactory { get; set; } =
            static (_, _, _) => Task.FromResult<CompanyAccessSessionContext?>(null);

        public Task<CompanyAccessSessionContext?> GetAsync(
            Guid userId,
            Guid? preferredActiveCompanyId,
            CancellationToken cancellationToken) =>
            ContextFactory(userId, preferredActiveCompanyId, cancellationToken);
    }

    private sealed class FakePlatformRuntimeStateRepository : IPlatformRuntimeStateRepository
    {
        public PlatformMaintenanceState? MaintenanceState { get; set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlatformMaintenanceState?> GetMaintenanceStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(MaintenanceState);

        public Task<PlatformMaintenanceState> UpsertMaintenanceStateAsync(
            PlatformMaintenanceState state,
            CancellationToken cancellationToken) =>
            Task.FromResult(MaintenanceState = state);

        public Task<PlatformNotificationReadinessState?> GetNotificationReadinessStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<PlatformNotificationReadinessState?>(null);

        public Task<PlatformNotificationReadinessState> UpsertNotificationReadinessStateAsync(
            PlatformNotificationReadinessState state,
            CancellationToken cancellationToken) =>
            Task.FromResult(state);
    }
}
