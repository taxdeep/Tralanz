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
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class CompanyOnboardingApiContractTests
{
    [Fact]
    public async Task GetSummary_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new CompanyOnboardingApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/company/onboarding/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSummary_ReturnsCompanyReadySummary_WhenAuthenticated()
    {
        using var factory = new CompanyOnboardingApplicationFactory();
        var companyId = Guid.Parse("376d5f6e-d5de-488d-b13f-257635f7455e");
        factory.BusinessSessions.ValidateResult = new PlatformBusinessSessionResult
        {
            Succeeded = true,
            UserId = Guid.Parse("940a3fc0-1dbf-4769-b0dd-d2d4a352e6ef"),
            ActiveCompanyId = companyId
        };
        factory.OnboardingStore.Summary = new WebShellCompanyOnboardingSummary
        {
            CompanyId = companyId,
            CompanyName = "Northwind Studio Ltd.",
            CompanyCode = "NORTHWIND",
            OwnerDisplayName = "Alice Rowan",
            OwnerEmail = "alice.rowan@northwind.example",
            TemplateKey = "ca_general_small_business",
            TemplateVersion = "2026.04.1",
            BaseCurrencyCode = "CAD",
            AccountCodeLength = 4,
            FirstTimeSetupCompletedAtUtc = new DateTimeOffset(2026, 4, 17, 8, 0, 0, TimeSpan.Zero),
            StarterAccountCodes = ["1000", "1200", "3000"],
            ReservedFamilies = ["1210-1249", "3010-3049"],
            StarterAccountCount = 9,
            HasPrimaryBook = true,
            StarterBankAccountCode = "1000",
            HasReceivableControlAccount = true,
            HasPayableControlAccount = true,
            ActiveTaxCodeCount = 0
        };

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-READY");

        var response = await client.GetAsync("/api/company/onboarding/summary");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<WebShellCompanyOnboardingSummary>();

        Assert.NotNull(payload);
        Assert.Equal(companyId, factory.OnboardingStore.LastRequestedCompanyId);
        Assert.Equal("NORTHWIND", payload!.CompanyCode);
        Assert.True(payload.RequiresOnboarding);
        Assert.True(payload.HasStarterChart);
        Assert.Equal("1000", payload.StarterBankAccountCode);
        Assert.Equal(0, payload.ActiveTaxCodeCount);
    }

    [Fact]
    public async Task Acknowledge_ReturnsUpdatedSummary_WhenAuthenticated()
    {
        using var factory = new CompanyOnboardingApplicationFactory();
        var userId = Guid.Parse("46b863af-c7ab-4721-ae83-e1fca1048665");
        var companyId = Guid.Parse("d2e60a82-5326-4d98-9920-3176ff57058e");
        factory.BusinessSessions.ValidateResult = new PlatformBusinessSessionResult
        {
            Succeeded = true,
            UserId = userId,
            ActiveCompanyId = companyId
        };
        factory.OnboardingStore.AcknowledgedSummary = new WebShellCompanyOnboardingSummary
        {
            CompanyId = companyId,
            CompanyName = "Northwind Studio Ltd.",
            CompanyCode = "NORTHWIND",
            OwnerDisplayName = "Alice Rowan",
            OwnerEmail = "alice.rowan@northwind.example",
            TemplateKey = "ca_general_small_business",
            TemplateVersion = "2026.04.1",
            BaseCurrencyCode = "CAD",
            AccountCodeLength = 4,
            FirstTimeSetupCompletedAtUtc = new DateTimeOffset(2026, 4, 17, 8, 0, 0, TimeSpan.Zero),
            FirstBusinessLoginAcknowledgedAtUtc = new DateTimeOffset(2026, 4, 17, 8, 30, 0, TimeSpan.Zero),
            StarterAccountCodes = ["1000", "1200", "3000"],
            ReservedFamilies = ["1210-1249", "3010-3049"],
            StarterAccountCount = 9,
            HasPrimaryBook = true,
            StarterBankAccountCode = "1000",
            HasReceivableControlAccount = true,
            HasPayableControlAccount = true,
            ActiveTaxCodeCount = 0
        };

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-ACK");

        var response = await client.PostAsJsonAsync("/api/company/onboarding/acknowledge", new { });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<WebShellCompanyOnboardingSummary>();

        Assert.NotNull(payload);
        Assert.Equal(companyId, factory.OnboardingStore.LastAcknowledgedCompanyId);
        Assert.Equal(userId, factory.OnboardingStore.LastAcknowledgedUserId);
        Assert.False(payload!.RequiresOnboarding);
        Assert.True(payload.HasStarterBankAccount);
    }

    private sealed class CompanyOnboardingApplicationFactory : WebApplicationFactory<global::Web.Shell.App>
    {
        public FakePlatformBusinessSessionRepository BusinessSessions { get; } = new();

        public FakeCompanyOnboardingStore OnboardingStore { get; } = new();

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
                    services.RemoveAll<IWebShellCompanyOnboardingStore>();
                    services.AddSingleton<IWebShellCompanyOnboardingStore>(OnboardingStore);
                });
        }
    }

    private sealed class FakePlatformBusinessSessionRepository : IPlatformBusinessSessionRepository
    {
        public PlatformBusinessSessionResult ValidateResult { get; set; } = new();

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlatformBusinessSessionResult> AuthenticateAsync(
            string login,
            string password,
            TimeSpan sessionLifetime,
            string? remoteIp,
            string? userAgent,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PlatformBusinessSessionResult());

        public Task<PlatformBusinessSessionResult> ValidateSessionAsync(
            string sessionToken,
            CancellationToken cancellationToken) =>
            Task.FromResult(ValidateResult);

        public Task<PlatformBusinessSessionResult> SwitchActiveCompanyAsync(
            string sessionToken,
            Guid activeCompanyId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PlatformBusinessSessionResult());

        public Task<PlatformBusinessSessionResult> CompleteSecondFactorAsync(
            Guid challengeId,
            string verificationCode,
            TimeSpan sessionLifetime,
            string? remoteIp,
            string? userAgent,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PlatformBusinessSessionResult());

        public Task RevokeSessionAsync(string sessionToken, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCompanyOnboardingStore : IWebShellCompanyOnboardingStore
    {
        public WebShellCompanyOnboardingSummary? Summary { get; set; }

        public WebShellCompanyOnboardingSummary? AcknowledgedSummary { get; set; }

        public Guid? LastRequestedCompanyId { get; private set; }

        public Guid? LastAcknowledgedCompanyId { get; private set; }

        public Guid? LastAcknowledgedUserId { get; private set; }

        public Task<WebShellCompanyOnboardingSummary?> GetAsync(Guid companyId, CancellationToken cancellationToken)
        {
            LastRequestedCompanyId = companyId;
            return Task.FromResult(Summary);
        }

        public Task<WebShellCompanyOnboardingSummary?> AcknowledgeAsync(Guid companyId, Guid userId, CancellationToken cancellationToken)
        {
            LastAcknowledgedCompanyId = companyId;
            LastAcknowledgedUserId = userId;
            return Task.FromResult(AcknowledgedSummary);
        }
    }
}
