using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Citus.Platform.Core.Services;
using Citus.SysAdmin.Api.Control;
using Citus.Ui.Shared.Control;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.CompanyAccess.Memberships;

namespace Citus.SysAdmin.Api.Tests;

public sealed class SysAdminNotificationReadinessApiContractTests
{
    [Fact]
    public async Task SetFirstCompanyDecision_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/setup/company-decision",
            new
            {
                createCompanyNow = false
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.RuntimeStateRepository.FirstCompanySetupState);
    }

    [Fact]
    public async Task SetFirstCompanyDecision_PersistsDeferredState_WhenAuthenticated()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        factory.AuthRepository.OnGetSetupStatus = cancellationToken =>
            Task.FromResult(
                new SysAdminSetupStatus
                {
                    AccountCount = 1,
                    FirstCompanySetupDeferred = factory.RuntimeStateRepository.FirstCompanySetupState?.IsDeferred == true,
                    FirstCompanySetupDeferredAtUtc = factory.RuntimeStateRepository.FirstCompanySetupState?.DeferredAtUtc
                });

        var response = await client.PostAsJsonAsync(
            "/auth/setup/company-decision",
            new
            {
                createCompanyNow = false
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<Citus.SysAdmin.Api.Auth.SysAdminSetupStatusResponse>();

        Assert.NotNull(payload);
        Assert.True(payload!.FirstCompanySetupDeferred);
        Assert.Equal("platform_ready_deferred", payload.SetupStage);
        Assert.NotNull(factory.RuntimeStateRepository.FirstCompanySetupState);
        Assert.True(factory.RuntimeStateRepository.FirstCompanySetupState!.IsDeferred);
        Assert.Equal(FakeSysAdminAuthRepository.ValidSessionToken, factory.AuthRepository.LastSessionToken);
    }

    [Fact]
    public async Task ProvisionFirstCompany_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/setup/first-company",
            new
            {
                ownerDisplayName = "Business Owner",
                ownerEmail = "owner@example.com",
                ownerPassword = "OwnerPass123!",
                companyName = "Northwind Studio Ltd.",
                entityType = "corporation",
                industry = "general_services",
                incorporatedOn = new DateTime(2026, 4, 16),
                fiscalYearEnd = "12-31",
                businessNumber = "BN-001",
                accountCodeLength = 4,
                phone = "604-555-0100",
                companyEmail = "hello@northwind.example",
                addressLine = "101 Harbor Street",
                city = "Vancouver",
                provinceState = "BC",
                postalCode = "V6B1A1",
                country = "Canada",
                templateKey = "ca_general_small_business",
                baseCurrencyCode = "CAD"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.FirstCompanyProvisioningRepository.LastCommand);
    }

    [Fact]
    public async Task ProvisionFirstCompany_ReturnsProvisioningPayload_WhenAuthenticated()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        factory.FirstCompanyProvisioningRepository.OnProvision = static (command, _) =>
            Task.FromResult(
                new PlatformFirstCompanyProvisioningResult
                {
                    Succeeded = true,
                    CompanyId = CompanyId.FromOrdinal(1),
                    CompanyEntityNumber = "EN202600000001",
                    CompanyName = command.CompanyName,
                    OwnerUserId = UserId.FromOrdinal(1),
                    OwnerEmail = command.OwnerEmail,
                    CompanyBookId = Guid.Parse("5c245d6a-a4da-4d75-a8f1-204f7d8f648f"),
                    CompanyBookCode = "PRIMARY",
                    TemplateKey = command.TemplateKey,
                    TemplateVersion = "2026.04",
                    BaseCurrencyCode = command.BaseCurrencyCode,
                    AccountCodeLength = command.AccountCodeLength,
                    StarterAccountCodes = ["10000", "12000", "30000"],
                    ReservedFamilies = ["1000-1099", "1210-1249"],
                    ProvisionedAtUtc = new DateTimeOffset(2026, 4, 16, 23, 0, 0, TimeSpan.Zero)
                });

        var response = await client.PostAsJsonAsync(
            "/auth/setup/first-company",
            new
            {
                ownerDisplayName = "Business Owner",
                ownerEmail = "owner@example.com",
                ownerPassword = "OwnerPass123!",
                companyName = "Northwind Studio Ltd.",
                entityType = "corporation",
                industry = "general_services",
                incorporatedOn = new DateTime(2026, 4, 16),
                fiscalYearEnd = "12-31",
                businessNumber = "BN-001",
                accountCodeLength = 5,
                phone = "",
                companyEmail = "hello@northwind.example",
                addressLine = "101 Harbor Street",
                city = "Vancouver",
                provinceState = "BC",
                postalCode = "V6B1A1",
                country = "Canada",
                templateKey = "ca_general_small_business",
                baseCurrencyCode = "CAD"
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<PlatformFirstCompanyProvisioningResult>();

        Assert.NotNull(payload);
        Assert.True(payload!.Succeeded);
        Assert.Equal("Northwind Studio Ltd.", payload.CompanyName);
        Assert.Equal("EN202600000001", payload.CompanyEntityNumber);
        Assert.NotNull(factory.FirstCompanyProvisioningRepository.LastCommand);
        Assert.Equal((object?)FakeSysAdminAuthRepository.ValidSysAdminAccountId, (object?)factory.FirstCompanyProvisioningRepository.LastCommand!.SysAdminAccountId);
        Assert.Equal("owner@example.com", factory.FirstCompanyProvisioningRepository.LastCommand.OwnerEmail);
        Assert.Equal("ca_general_small_business", factory.FirstCompanyProvisioningRepository.LastCommand.TemplateKey);
        Assert.Equal(5, factory.FirstCompanyProvisioningRepository.LastCommand.AccountCodeLength);
        Assert.Equal(string.Empty, factory.FirstCompanyProvisioningRepository.LastCommand.Phone);
        Assert.Contains("10000", payload.StarterAccountCodes);
    }

    [Theory]
    [InlineData("10010", 4, "1001")]
    [InlineData("10010", 5, "10010")]
    [InlineData("10010", 6, "100100")]
    [InlineData("30000", 5, "30000")]
    public void FirstCompanyTemplateAccountCodes_AreRightPadded_ToSelectedLength(
        string canonicalCode,
        int accountCodeLength,
        string expectedCode)
    {
        var repositoryType = Type.GetType(
            "Citus.Platform.Infrastructure.Persistence.PostgresPlatformFirstCompanyProvisioningRepository, Citus.Platform.Infrastructure",
            throwOnError: true)!;
        var formatter = repositoryType.GetMethod(
            "FormatAccountCode",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(formatter);
        Assert.Equal(expectedCode, formatter!.Invoke(null, [canonicalCode, accountCodeLength]));
    }

    [Fact]
    public async Task GetNotificationReadiness_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/control/notification-readiness");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.NotificationWorkflow.GetCallCount);
        Assert.Equal(string.Empty, factory.AuthRepository.LastSessionToken);
    }

    [Fact]
    public async Task GetNotificationReadiness_ReturnsWorkflowSummary_WhenAuthenticated()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        factory.NotificationWorkflow.OnGet = static _ =>
            Task.FromResult(
                new PlatformNotificationReadinessReport
                {
                    ConfigPresent = true,
                    TestStatus = "passed",
                    VerificationReady = true,
                    IsVerificationDeliveryReady = true,
                    BlockingReason = string.Empty,
                    ConfigurationError = string.Empty,
                    LastTestedAtUtc = new DateTimeOffset(2026, 4, 16, 21, 10, 0, TimeSpan.Zero)
                });

        var response = await client.GetAsync("/control/notification-readiness");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<NotificationReadinessSummary>();

        Assert.NotNull(payload);
        Assert.Equal(FakeSysAdminAuthRepository.ValidSessionToken, factory.AuthRepository.LastSessionToken);
        Assert.Equal(1, factory.NotificationWorkflow.GetCallCount);
        Assert.True(payload!.IsVerificationDeliveryReady);
        Assert.Equal("passed", payload.TestStatus);
    }

    [Fact]
    public async Task UpdateNotificationReadiness_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            "/control/notification-readiness",
            new NotificationReadinessUpdateRequest
            {
                ConfigPresent = true,
                TestStatus = "passed",
                VerificationReady = true
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.RuntimeStateRepository.LastUpsertedNotificationReadinessState);
        Assert.Equal(0, factory.NotificationWorkflow.GetCallCount);
    }

    [Fact]
    public async Task UpdateNotificationReadiness_PersistsState_AndReturnsWorkflowSummary_WhenAuthenticated()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        factory.NotificationWorkflow.OnGet = static _ =>
            Task.FromResult(
                new PlatformNotificationReadinessReport
                {
                    ConfigPresent = true,
                    TestStatus = "passed",
                    VerificationReady = true,
                    IsVerificationDeliveryReady = false,
                    BlockingReason = "Provider configuration drift detected.",
                    ConfigurationError = "SMTP username is missing.",
                    LastTestedAtUtc = new DateTimeOffset(2026, 4, 16, 21, 20, 0, TimeSpan.Zero)
                });

        var response = await client.PutAsJsonAsync(
            "/control/notification-readiness",
            new NotificationReadinessUpdateRequest
            {
                ConfigPresent = true,
                TestStatus = "passed",
                LastTestedAtUtc = new DateTimeOffset(2026, 4, 16, 21, 15, 0, TimeSpan.Zero),
                VerificationReady = true
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<NotificationReadinessSummary>();

        Assert.NotNull(payload);
        Assert.Equal(FakeSysAdminAuthRepository.ValidSessionToken, factory.AuthRepository.LastSessionToken);
        Assert.Equal(1, factory.NotificationWorkflow.GetCallCount);

        var savedState = factory.RuntimeStateRepository.LastUpsertedNotificationReadinessState;
        Assert.NotNull(savedState);
        Assert.True(savedState!.ConfigPresent);
        Assert.Equal("passed", savedState.TestStatus);
        Assert.True(savedState.VerificationReady);
        Assert.Equal(new DateTimeOffset(2026, 4, 16, 21, 15, 0, TimeSpan.Zero), savedState.LastTestedAtUtc);

        Assert.False(payload!.IsVerificationDeliveryReady);
        Assert.Equal("Provider configuration drift detected.", payload.BlockingReason);
        Assert.Equal("SMTP username is missing.", payload.ConfigurationError);
        Assert.Equal(new DateTimeOffset(2026, 4, 16, 21, 20, 0, TimeSpan.Zero), payload.LastTestedAtUtc);
    }

    [Fact]
    public async Task SetCompanyStatus_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var companyId = CompanyId.FromOrdinal(1);
        var response = await client.PutAsJsonAsync(
            $"/control/companies/{companyId}/status",
            new CompanyStatusUpdateRequest
            {
                Status = "inactive",
                Reason = "Compliance hold"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.GovernanceRepository.LastCompanyStatusCompanyId);
    }

    [Fact]
    public async Task SetCompanyStatus_ReturnsOkPayload_WhenGovernanceSucceeds()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var companyId = CompanyId.FromOrdinal(1);
        factory.GovernanceRepository.OnSetCompanyStatus = static (requestedCompanyId, status, reason, _, _) =>
            Task.FromResult<CompanyStatusGovernanceResult?>(
                new CompanyStatusGovernanceResult
                {
                    CompanyId = requestedCompanyId,
                    EntityNumber = "EN202600000123",
                    LegalName = "Northwind Studio Ltd.",
                    PreviousStatus = "active",
                    Status = status,
                    Reason = reason,
                    UpdatedAtUtc = new DateTimeOffset(2026, 4, 16, 22, 20, 0, TimeSpan.Zero)
                });

        var response = await client.PutAsJsonAsync(
            $"/control/companies/{companyId}/status",
            new CompanyStatusUpdateRequest
            {
                Status = "inactive",
                Reason = "Compliance hold",
                SysAdminAccountId = UserId.FromOrdinal(1)
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<CompanyStatusGovernanceResult>();

        Assert.NotNull(payload);
        Assert.Equal((object?)companyId, (object?)factory.GovernanceRepository.LastCompanyStatusCompanyId);
        Assert.Equal("inactive", factory.GovernanceRepository.LastCompanyStatusStatus);
        Assert.Equal("Compliance hold", factory.GovernanceRepository.LastCompanyStatusReason);
        Assert.Equal((object?)FakeSysAdminAuthRepository.ValidSysAdminAccountId, (object?)factory.GovernanceRepository.LastCompanyStatusSysAdminAccountId);
        Assert.Equal("Northwind Studio Ltd.", payload!.LegalName);
        Assert.Equal("inactive", payload.Status);
    }

    [Fact]
    public async Task SetCompanyStatus_ReturnsNotFound_WhenGovernanceReturnsNull()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var companyId = CompanyId.FromOrdinal(1);
        factory.GovernanceRepository.OnSetCompanyStatus = static (_, _, _, _, _) =>
            Task.FromResult<CompanyStatusGovernanceResult?>(null);

        var response = await client.PutAsJsonAsync(
            $"/control/companies/{companyId}/status",
            new CompanyStatusUpdateRequest
            {
                Status = "inactive",
                Reason = "Missing company"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            $"Company '{companyId}' was not found.",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SetCompanyStatus_ReturnsBadRequest_WhenGovernanceRejectsRequest()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var companyId = CompanyId.FromOrdinal(1);
        factory.GovernanceRepository.OnSetCompanyStatus = static (_, _, _, _, _) =>
            throw new InvalidOperationException("Company status transition is not allowed from archived to active.");

        var response = await client.PutAsJsonAsync(
            $"/control/companies/{companyId}/status",
            new CompanyStatusUpdateRequest
            {
                Status = "active",
                Reason = "Illegal transition"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Company status transition is not allowed from archived to active.",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SetAccountStatus_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var accountId = UserId.FromOrdinal(1);
        var response = await client.PutAsJsonAsync(
            $"/control/accounts/{accountId}/status",
            new AccountStatusUpdateRequest
            {
                Status = "locked",
                LockedUntilUtc = new DateTimeOffset(2026, 4, 20, 8, 0, 0, TimeSpan.Zero),
                Reason = "Fraud review"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.GovernanceRepository.LastAccountStatusAccountId);
    }

    [Fact]
    public async Task SetAccountStatus_ReturnsOkPayload_WhenGovernanceSucceeds()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        var lockedUntilUtc = new DateTimeOffset(2026, 4, 20, 8, 0, 0, TimeSpan.Zero);
        factory.GovernanceRepository.OnSetAccountStatus = static (requestedAccountId, status, lockedUntil, reason, _, _) =>
            Task.FromResult<AccountStatusGovernanceResult?>(
                new AccountStatusGovernanceResult
                {
                    AccountId = requestedAccountId,
                    Email = "user@example.com",
                    Username = "user.one",
                    PreviousStatus = "active",
                    Status = status,
                    LockedUntilUtc = lockedUntil,
                    Reason = reason,
                    UpdatedAtUtc = new DateTimeOffset(2026, 4, 16, 22, 30, 0, TimeSpan.Zero)
                });

        var response = await client.PutAsJsonAsync(
            $"/control/accounts/{accountId}/status",
            new AccountStatusUpdateRequest
            {
                Status = "locked",
                LockedUntilUtc = lockedUntilUtc,
                Reason = "Fraud review",
                SysAdminAccountId = UserId.FromOrdinal(1)
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AccountStatusGovernanceResult>();

        Assert.NotNull(payload);
        Assert.Equal((object?)accountId, (object?)factory.GovernanceRepository.LastAccountStatusAccountId);
        Assert.Equal("locked", factory.GovernanceRepository.LastAccountStatusStatus);
        Assert.Equal(lockedUntilUtc, factory.GovernanceRepository.LastAccountStatusLockedUntilUtc);
        Assert.Equal("Fraud review", factory.GovernanceRepository.LastAccountStatusReason);
        Assert.Equal((object?)FakeSysAdminAuthRepository.ValidSysAdminAccountId, (object?)factory.GovernanceRepository.LastAccountStatusSysAdminAccountId);
        Assert.Equal("user@example.com", payload!.Email);
        Assert.Equal("locked", payload.Status);
    }

    [Fact]
    public async Task SetAccountStatus_ReturnsNotFound_WhenGovernanceReturnsNull()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnSetAccountStatus = static (_, _, _, _, _, _) =>
            Task.FromResult<AccountStatusGovernanceResult?>(null);

        var response = await client.PutAsJsonAsync(
            $"/control/accounts/{accountId}/status",
            new AccountStatusUpdateRequest
            {
                Status = "disabled",
                Reason = "Missing account"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            $"Account '{accountId}' was not found.",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SetAccountStatus_ReturnsBadRequest_WhenGovernanceRejectsRequest()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnSetAccountStatus = static (_, _, _, _, _, _) =>
            throw new InvalidOperationException("Account status transition is not allowed while a password reset request is pending.");

        var response = await client.PutAsJsonAsync(
            $"/control/accounts/{accountId}/status",
            new AccountStatusUpdateRequest
            {
                Status = "disabled",
                Reason = "Illegal transition"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Account status transition is not allowed while a password reset request is pending.",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ListUsers_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/control/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.GovernanceRepository.ListManagedUsersCallCount);
    }

    [Fact]
    public async Task ListUsers_ReturnsManagedUserReadModel_WhenAuthenticated()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        factory.GovernanceRepository.OnListManagedUsers = static _ =>
            Task.FromResult<IReadOnlyList<ManagedPlatformAccountSummary>>(
            [
                new ManagedPlatformAccountSummary
                {
                    AccountId = UserId.FromOrdinal(1),
                    DisplayName = "Morgan Hale",
                    Email = "user@example.com",
                    Username = "user.one",
                    Status = "active",
                    MfaMode = "email_code",
                    ActiveMfaRecoveryStatus = "approved",
                    LastMfaResetAtUtc = new DateTimeOffset(2026, 4, 16, 23, 30, 0, TimeSpan.Zero),
                    LastMfaResetReason = "Operator recovery reset",
                    CompanyCodes = ["EN202600000123"]
                }
            ]);

        var response = await client.GetAsync("/control/users");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<ManagedUserSummary>>();

        Assert.NotNull(payload);
        Assert.Single(payload!);
        Assert.Equal(1, factory.GovernanceRepository.ListManagedUsersCallCount);
        Assert.Equal("Morgan Hale", payload[0].DisplayName);
        Assert.Equal("email_code", payload[0].MfaMode);
        Assert.Equal("approved", payload[0].ActiveMfaRecoveryStatus);
        Assert.True(payload[0].HasActiveMfaRecoveryRequest);
        Assert.False(payload[0].CanEmergencyMfaReset);
        Assert.Contains("blocked", payload[0].EmergencyMfaResetPolicyReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Operator recovery reset", payload[0].LastMfaResetReason);
    }

    [Fact]
    public async Task ListAuditEvents_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/control/audit-events?limit=7");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.GovernanceRepository.LastAuditLimit);
    }

    [Fact]
    public async Task ListAuditEvents_ReturnsMappedSummaries_WhenAuthenticated()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var auditId = Guid.Parse("67f94677-64d8-4c07-9383-fcb87dc67ba0");
        var entityId = Guid.Parse("75299d36-d88d-4bd0-8c0d-caf2520d5f31");
        var companyId = CompanyId.FromOrdinal(1);
        factory.GovernanceRepository.OnListRecentAuditEvents = static (limit, _) =>
            Task.FromResult<IReadOnlyList<PlatformAuditEvent>>(
            [
                new PlatformAuditEvent
                {
                    AuditId = Guid.Parse("67f94677-64d8-4c07-9383-fcb87dc67ba0"),
                    CompanyId = CompanyId.FromOrdinal(1),
                    CompanyCode = "NORTHWIND",
                    CompanyName = "Northwind Studio Ltd.",
                    ScopeLabel = "Northwind Studio Ltd. (NORTHWIND)",
                    ActorType = "sysadmin",
                    ActorId = FakeSysAdminAuthRepository.ValidSysAdminAccountId,
                    ActorDisplayName = "Platform Operator",
                    ActorEmail = "sysadmin@example.com",
                    EntityType = "membership",
                    EntityId = Guid.Parse("75299d36-d88d-4bd0-8c0d-caf2520d5f31"),
                    EntityLabel = "Morgan Hale",
                    Action = "membership_role_changed",
                    ActionLabel = "Membership Role Changed",
                    Detail = "Role changed from user to owner.",
                    Reason = "Owner recovery",
                    Highlights = ["user", "owner"],
                    CreatedAtUtc = new DateTimeOffset(2026, 4, 16, 22, 45, 0, TimeSpan.Zero)
                }
            ]);

        var response = await client.GetAsync("/control/audit-events?limit=7");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<PlatformAuditEventSummary>>();

        Assert.NotNull(payload);
        Assert.Equal(7, factory.GovernanceRepository.LastAuditLimit);
        Assert.Single(payload!);
        Assert.Equal(auditId, payload[0].AuditId);
        Assert.Equal((object?)companyId, (object?)payload[0].CompanyId);
        Assert.Equal(entityId, payload[0].EntityId);
        Assert.Equal("membership_role_changed", payload[0].Action);
        Assert.Equal("Membership Role Changed", payload[0].ActionLabel);
    }

    [Fact]
    public async Task ChangeMembershipRole_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var companyId = CompanyId.FromOrdinal(1);
        var membershipId = Guid.Parse("75299d36-d88d-4bd0-8c0d-caf2520d5f31");
        var response = await client.PutAsJsonAsync(
            $"/control/companies/{companyId}/memberships/{membershipId}/role",
            new CompanyMembershipRoleUpdateRequest
            {
                Role = "owner",
                Reason = "Owner recovery"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.MembershipGovernanceWorkflow.LastCompanyId);
    }

    [Fact]
    public async Task ChangeMembershipRole_ReturnsOkPayload_WhenWorkflowSucceeds()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var companyId = CompanyId.FromOrdinal(1);
        var membershipId = Guid.Parse("75299d36-d88d-4bd0-8c0d-caf2520d5f31");
        var accountId = UserId.FromOrdinal(1);
        factory.MembershipGovernanceWorkflow.OnChangeRole = static (requestedCompanyId, requestedMembershipId, role, reason, _, _) =>
            Task.FromResult(
                new CompanyMembershipRoleChangeResult
                {
                    CompanyId = requestedCompanyId,
                    MembershipId = requestedMembershipId,
                    AccountId = UserId.FromOrdinal(1),
                    Email = "user@example.com",
                    Username = "user.one",
                    PreviousRole = "user",
                    Role = role,
                    Reason = reason,
                    UpdatedAtUtc = new DateTimeOffset(2026, 4, 16, 22, 50, 0, TimeSpan.Zero)
                });

        var response = await client.PutAsJsonAsync(
            $"/control/companies/{companyId}/memberships/{membershipId}/role",
            new CompanyMembershipRoleUpdateRequest
            {
                Role = "owner",
                Reason = "Owner recovery",
                SysAdminAccountId = UserId.FromOrdinal(1)
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<CompanyMembershipRoleChangeResult>();

        Assert.NotNull(payload);
        Assert.Equal((object?)companyId, (object?)factory.MembershipGovernanceWorkflow.LastCompanyId);
        Assert.Equal(membershipId, factory.MembershipGovernanceWorkflow.LastMembershipId);
        Assert.Equal("owner", factory.MembershipGovernanceWorkflow.LastRole);
        Assert.Equal("Owner recovery", factory.MembershipGovernanceWorkflow.LastReason);
        Assert.Equal((object?)FakeSysAdminAuthRepository.ValidSysAdminAccountId, (object?)factory.MembershipGovernanceWorkflow.LastSysAdminAccountId);
        Assert.Equal((object?)accountId, (object?)payload!.AccountId);
        Assert.Equal("owner", payload.Role);
        Assert.Equal("user", payload.PreviousRole);
    }

    [Fact]
    public async Task ChangeMembershipRole_ReturnsBadRequest_WhenWorkflowRejectsRequest()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var companyId = CompanyId.FromOrdinal(1);
        var membershipId = Guid.Parse("75299d36-d88d-4bd0-8c0d-caf2520d5f31");
        factory.MembershipGovernanceWorkflow.OnChangeRole = static (_, _, _, _, _, _) =>
            throw new InvalidOperationException("At least one owner must remain assigned to the company.");

        var response = await client.PutAsJsonAsync(
            $"/control/companies/{companyId}/memberships/{membershipId}/role",
            new CompanyMembershipRoleUpdateRequest
            {
                Role = "user",
                Reason = "Illegal downgrade"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "At least one owner must remain assigned to the company.",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RequestPasswordReset_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var accountId = UserId.FromOrdinal(1);
        var response = await client.PostAsJsonAsync(
            $"/control/accounts/{accountId}/password-reset-requests",
            new PasswordResetRequestCommand
            {
                Reason = "Operator reset request"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.GovernanceRepository.LastPasswordResetAccountId);
    }

    [Fact]
    public async Task RequestPasswordReset_ReturnsAcceptedPayload_WhenGovernanceSucceeds()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        var requestId = Guid.Parse("f48b5f2c-a4ca-42c3-a3cf-c2e2b4444a6e");
        factory.GovernanceRepository.OnRequestPasswordReset = static (requestedAccountId, reason, sysAdminAccountId, _) =>
            Task.FromResult<PasswordResetGovernanceResult?>(
                new PasswordResetGovernanceResult
                {
                    RequestId = Guid.Parse("f48b5f2c-a4ca-42c3-a3cf-c2e2b4444a6e"),
                    AccountId = requestedAccountId,
                    Email = "user@example.com",
                    Username = "user.one",
                    DeliveryStatus = "queued",
                    Reason = reason,
                    RequestedAtUtc = new DateTimeOffset(2026, 4, 16, 22, 0, 0, TimeSpan.Zero)
                });

        var response = await client.PostAsJsonAsync(
            $"/control/accounts/{accountId}/password-reset-requests",
            new PasswordResetRequestCommand
            {
                Reason = "Operator reset request",
                SysAdminAccountId = UserId.FromOrdinal(1)
            });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/control/accounts/{accountId}/password-reset-requests/{requestId}", response.Headers.Location?.OriginalString);

        var payload = await response.Content.ReadFromJsonAsync<PasswordResetGovernanceResult>();

        Assert.NotNull(payload);
        Assert.Equal((object?)accountId, (object?)factory.GovernanceRepository.LastPasswordResetAccountId);
        Assert.Equal("Operator reset request", factory.GovernanceRepository.LastPasswordResetReason);
        Assert.Equal((object?)FakeSysAdminAuthRepository.ValidSysAdminAccountId, (object?)factory.GovernanceRepository.LastPasswordResetSysAdminAccountId);
        Assert.Equal("queued", payload!.DeliveryStatus);
        Assert.Equal("user@example.com", payload.Email);
    }

    [Fact]
    public async Task RequestPasswordReset_ReturnsNotFound_WhenGovernanceReturnsNull()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnRequestPasswordReset = static (_, _, _, _) =>
            Task.FromResult<PasswordResetGovernanceResult?>(null);

        var response = await client.PostAsJsonAsync(
            $"/control/accounts/{accountId}/password-reset-requests",
            new PasswordResetRequestCommand
            {
                Reason = "Missing account"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            $"Account '{accountId}' was not found.",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RequestPasswordReset_ReturnsBadRequest_WhenGovernanceRejectsRequest()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnRequestPasswordReset = static (_, _, _, _) =>
            throw new InvalidOperationException("Password reset is blocked because notification readiness is not verified.");

        var response = await client.PostAsJsonAsync(
            $"/control/accounts/{accountId}/password-reset-requests",
            new PasswordResetRequestCommand
            {
                Reason = "Blocked reset"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Password reset is blocked because notification readiness is not verified.",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RequestPasswordReset_ReturnsServiceUnavailable_WhenDeliveryFails()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnRequestPasswordReset = static (_, _, _, _) =>
            throw new PlatformNotificationDeliveryException("SMTP provider rejected the password reset dispatch.");

        var response = await client.PostAsJsonAsync(
            $"/control/accounts/{accountId}/password-reset-requests",
            new PasswordResetRequestCommand
            {
                Reason = "Delivery failure"
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Password reset delivery failed.", document.RootElement.GetProperty("title").GetString());
        Assert.Equal("SMTP provider rejected the password reset dispatch.", document.RootElement.GetProperty("detail").GetString());
        Assert.Equal(503, document.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ResetAccountMfa_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var accountId = UserId.FromOrdinal(1);
        var response = await client.PostAsJsonAsync(
            $"/control/accounts/{accountId}/mfa-reset",
            new AccountMfaResetRequest
            {
                Reason = "Operator MFA reset"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.GovernanceRepository.LastMfaResetAccountId);
    }

    [Fact]
    public async Task ResetAccountMfa_ReturnsOkPayload_WhenGovernanceSucceeds()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnResetAccountMfa = static (requestedAccountId, reason, sysAdminAccountId, _) =>
            Task.FromResult<AccountMfaResetGovernanceResult?>(
                new AccountMfaResetGovernanceResult
                {
                    AccountId = requestedAccountId,
                    Email = "user@example.com",
                    Username = "user.one",
                    PreviousMfaMode = "email_code",
                    MfaMode = "none",
                    RevokedChallengeCount = 2,
                    Reason = reason,
                    UpdatedAtUtc = new DateTimeOffset(2026, 4, 16, 23, 15, 0, TimeSpan.Zero)
                });

        var response = await client.PostAsJsonAsync(
            $"/control/accounts/{accountId}/mfa-reset",
            new AccountMfaResetRequest
            {
                Reason = "Operator MFA reset",
                SysAdminAccountId = UserId.FromOrdinal(1)
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AccountMfaResetGovernanceResult>();

        Assert.NotNull(payload);
        Assert.Equal((object?)accountId, (object?)factory.GovernanceRepository.LastMfaResetAccountId);
        Assert.Equal("Operator MFA reset", factory.GovernanceRepository.LastMfaResetReason);
        Assert.Equal((object?)FakeSysAdminAuthRepository.ValidSysAdminAccountId, (object?)factory.GovernanceRepository.LastMfaResetSysAdminAccountId);
        Assert.Equal("user@example.com", payload!.Email);
        Assert.Equal("email_code", payload.PreviousMfaMode);
        Assert.Equal("none", payload.MfaMode);
        Assert.Equal(2, payload.RevokedChallengeCount);
    }

    [Fact]
    public async Task ResetAccountMfa_ReturnsNotFound_WhenGovernanceReturnsNull()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnResetAccountMfa = static (_, _, _, _) =>
            Task.FromResult<AccountMfaResetGovernanceResult?>(null);

        var response = await client.PostAsJsonAsync(
            $"/control/accounts/{accountId}/mfa-reset",
            new AccountMfaResetRequest
            {
                Reason = "Missing account"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            $"Account '{accountId}' was not found.",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ResetAccountMfa_ReturnsBadRequest_WhenGovernanceRejectsRequest()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnResetAccountMfa = static (_, _, _, _) =>
            throw new InvalidOperationException("MFA reset is blocked because the account has a pending recovery review.");

        var response = await client.PostAsJsonAsync(
            $"/control/accounts/{accountId}/mfa-reset",
            new AccountMfaResetRequest
            {
                Reason = "Blocked MFA reset"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "MFA reset is blocked because the account has a pending recovery review.",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ListOpenMfaRecoveryRequests_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/control/mfa-recovery-requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.GovernanceRepository.ListOpenMfaRecoveryRequestsCallCount);
    }

    [Fact]
    public async Task ListOpenMfaRecoveryRequests_ReturnsQueue_WhenAuthenticated()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        factory.GovernanceRepository.OnListOpenMfaRecoveryRequests = static _ =>
            Task.FromResult<IReadOnlyList<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>>(
            [
                new Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary
                {
                    RequestId = Guid.Parse("5ee30770-a50e-4f19-a27f-8990202f8117"),
                    AccountId = UserId.FromOrdinal(1),
                    DisplayName = "Morgan Hale",
                    Email = "user@example.com",
                    Username = "user.one",
                    CurrentMfaMode = "email_code",
                    Status = "requested",
                    RequestReason = "Lost access to verified mailbox device.",
                    RequestedAtUtc = new DateTimeOffset(2026, 4, 16, 23, 20, 0, TimeSpan.Zero)
                }
            ]);

        var response = await client.GetAsync("/control/mfa-recovery-requests");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<Citus.Ui.Shared.Control.MfaRecoveryRequestSummary>>();

        Assert.NotNull(payload);
        Assert.Single(payload!);
        Assert.Equal(1, factory.GovernanceRepository.ListOpenMfaRecoveryRequestsCallCount);
        Assert.Equal("Morgan Hale", payload[0].DisplayName);
        Assert.Equal("requested", payload[0].Status);
    }

    [Fact]
    public async Task ListAccountMfaTimeline_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var accountId = UserId.FromOrdinal(1);
        var response = await client.GetAsync($"/control/accounts/{accountId}/mfa-timeline");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.GovernanceRepository.LastMfaTimelineAccountId);
    }

    [Fact]
    public async Task ListAccountMfaTimeline_ReturnsTimeline_WhenAuthenticated()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnListAccountMfaTimeline = static (_, _, _) =>
            Task.FromResult<IReadOnlyList<PlatformAuditEvent>>(
            [
                new PlatformAuditEvent
                {
                    Action = "account_mfa_recovery_requested",
                    ActionLabel = "Account MFA Recovery Requested",
                    Detail = "email_code | requested",
                    Reason = "Lost access to verified mailbox device.",
                    ActorType = "user",
                    ActorDisplayName = "Morgan Hale",
                    CreatedAtUtc = new DateTimeOffset(2026, 4, 16, 23, 55, 0, TimeSpan.Zero)
                }
            ]);

        var response = await client.GetAsync($"/control/accounts/{accountId}/mfa-timeline?limit=7");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<PlatformMfaTimelineEntrySummary>>();

        Assert.NotNull(payload);
        Assert.Single(payload!);
        Assert.Equal((object?)accountId, (object?)factory.GovernanceRepository.LastMfaTimelineAccountId);
        Assert.Equal(7, factory.GovernanceRepository.LastMfaTimelineLimit);
        Assert.Equal("Account MFA Recovery Requested", payload[0].ActionLabel);
        Assert.Equal("Morgan Hale", payload[0].ActorDisplayName);
    }

    [Fact]
    public async Task ListAccountMfaRecoveryHistory_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var accountId = UserId.FromOrdinal(1);
        var response = await client.GetAsync($"/control/accounts/{accountId}/mfa-recovery-history");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.GovernanceRepository.LastMfaRecoveryHistoryAccountId);
    }

    [Fact]
    public async Task ListAccountMfaRecoveryHistory_ReturnsHistory_WhenAuthenticated()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnListAccountMfaRecoveryHistory = static (requestedAccountId, _, _) =>
            Task.FromResult<IReadOnlyList<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>>(
            [
                new Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary
                {
                    RequestId = Guid.Parse("3f6701b7-bd37-44ff-8f82-7c6324b2e1d0"),
                    AccountId = requestedAccountId,
                    DisplayName = "Morgan Hale",
                    Email = "user@example.com",
                    Username = "user.one",
                    CurrentMfaMode = "email_code",
                    Status = "executed",
                    RequestReason = "Lost access to verified mailbox device.",
                    RequestedAtUtc = new DateTimeOffset(2026, 4, 16, 23, 20, 0, TimeSpan.Zero),
                    ReviewReason = "Identity verified over support callback.",
                    ReviewedAtUtc = new DateTimeOffset(2026, 4, 16, 23, 30, 0, TimeSpan.Zero),
                    ReviewedByDisplayName = "SysAdmin Operator",
                    ExecutionReason = "Approved MFA recovery executed by SysAdmin.",
                    ExecutedAtUtc = new DateTimeOffset(2026, 4, 16, 23, 40, 0, TimeSpan.Zero),
                    ExecutedByDisplayName = "SysAdmin Operator"
                }
            ]);

        var response = await client.GetAsync($"/control/accounts/{accountId}/mfa-recovery-history?limit=6");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<Citus.Ui.Shared.Control.MfaRecoveryRequestSummary>>();

        Assert.NotNull(payload);
        Assert.Single(payload!);
        Assert.Equal((object?)accountId, (object?)factory.GovernanceRepository.LastMfaRecoveryHistoryAccountId);
        Assert.Equal(6, factory.GovernanceRepository.LastMfaRecoveryHistoryLimit);
        Assert.Equal("executed", payload[0].Status);
        Assert.Equal("Approved MFA recovery executed by SysAdmin.", payload[0].ExecutionReason);
        Assert.Equal("SysAdmin Operator", payload[0].ExecutedByDisplayName);
        Assert.Equal(new DateTimeOffset(2026, 4, 16, 23, 40, 0, TimeSpan.Zero), payload[0].ExecutedAtUtc);
    }

    [Fact]
    public async Task ReviewMfaRecoveryRequest_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var requestId = Guid.Parse("5ee30770-a50e-4f19-a27f-8990202f8117");
        var response = await client.PutAsJsonAsync(
            $"/control/mfa-recovery-requests/{requestId}/decision",
            new MfaRecoveryReviewRequest
            {
                Decision = "approve",
                Reason = "Approved recovery."
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.GovernanceRepository.LastReviewedMfaRecoveryRequestId);
    }

    [Fact]
    public async Task ReviewMfaRecoveryRequest_ReturnsOkPayload_WhenGovernanceSucceeds()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var requestId = Guid.Parse("5ee30770-a50e-4f19-a27f-8990202f8117");
        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnReviewMfaRecoveryRequest = (requestedRequestId, decision, reason, _, _) =>
            Task.FromResult<MfaRecoveryReviewResult?>(
                new MfaRecoveryReviewResult
                {
                    RequestId = requestedRequestId,
                    AccountId = accountId,
                    Status = decision == "approve" ? "approved" : "rejected",
                    ReviewReason = reason,
                    ReviewedAtUtc = new DateTimeOffset(2026, 4, 16, 23, 40, 0, TimeSpan.Zero)
                });

        var response = await client.PutAsJsonAsync(
            $"/control/mfa-recovery-requests/{requestId}/decision",
            new MfaRecoveryReviewRequest
            {
                Decision = "approve",
                Reason = "Recovery evidence checked.",
                SysAdminAccountId = UserId.FromOrdinal(1)
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<MfaRecoveryReviewResult>();

        Assert.NotNull(payload);
        Assert.Equal(requestId, factory.GovernanceRepository.LastReviewedMfaRecoveryRequestId);
        Assert.Equal("approve", factory.GovernanceRepository.LastReviewedMfaRecoveryDecision);
        Assert.Equal("Recovery evidence checked.", factory.GovernanceRepository.LastReviewedMfaRecoveryReason);
        Assert.Equal((object?)FakeSysAdminAuthRepository.ValidSysAdminAccountId, (object?)factory.GovernanceRepository.LastReviewedMfaRecoverySysAdminAccountId);
        Assert.Equal("approved", payload!.Status);
    }

    [Fact]
    public async Task ExecuteMfaRecoveryRequest_ReturnsOkPayload_WhenGovernanceSucceeds()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        var requestId = Guid.Parse("5ee30770-a50e-4f19-a27f-8990202f8117");
        var accountId = UserId.FromOrdinal(1);
        factory.GovernanceRepository.OnExecuteMfaRecoveryRequest = (requestedRequestId, reason, _, _) =>
            Task.FromResult<MfaRecoveryExecutionResult?>(
                new MfaRecoveryExecutionResult
                {
                    RequestId = requestedRequestId,
                    AccountId = accountId,
                    PreviousMfaMode = "email_code",
                    MfaMode = "none",
                    RevokedChallengeCount = 3,
                    ExecutionReason = reason,
                    ExecutedAtUtc = new DateTimeOffset(2026, 4, 16, 23, 50, 0, TimeSpan.Zero)
                });

        var response = await client.PostAsJsonAsync(
            $"/control/mfa-recovery-requests/{requestId}/execute",
            new MfaRecoveryExecuteRequest
            {
                Reason = "Approved recovery executed by operator.",
                SysAdminAccountId = UserId.FromOrdinal(1)
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<MfaRecoveryExecutionResult>();

        Assert.NotNull(payload);
        Assert.Equal(requestId, factory.GovernanceRepository.LastExecutedMfaRecoveryRequestId);
        Assert.Equal("Approved recovery executed by operator.", factory.GovernanceRepository.LastExecutedMfaRecoveryReason);
        Assert.Equal((object?)FakeSysAdminAuthRepository.ValidSysAdminAccountId, (object?)factory.GovernanceRepository.LastExecutedMfaRecoverySysAdminAccountId);
        Assert.Equal("none", payload!.MfaMode);
        Assert.Equal(3, payload.RevokedChallengeCount);
    }

    [Fact]
    public async Task SendNotificationTest_ReturnsUnauthorized_WhenSessionHeaderMissing()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/control/notification-readiness/test-send",
            new NotificationTestSendRequest
            {
                Destination = "ops@example.com"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.NotificationWorkflow.SendTestCallCount);
        Assert.Equal(string.Empty, factory.AuthRepository.LastSessionToken);
    }

    [Fact]
    public async Task SendNotificationTest_ReturnsPayload_AndUsesSessionDisplayNameFallback()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        factory.NotificationWorkflow.OnSendTest = static (destination, recipientDisplayName, _) =>
            Task.FromResult(
                new PlatformNotificationTestSendResult
                {
                    Succeeded = true,
                    ProviderKey = "smtp",
                    Destination = destination,
                    Readiness = new PlatformNotificationReadinessReport
                    {
                        ConfigPresent = true,
                        TestStatus = "passed",
                        VerificationReady = true,
                        IsVerificationDeliveryReady = true
                    }
                });

        var response = await client.PostAsJsonAsync(
            "/control/notification-readiness/test-send",
            new NotificationTestSendRequest
            {
                Destination = "ops@example.com",
                RecipientDisplayName = string.Empty
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<NotificationTestSendResult>();

        Assert.NotNull(payload);
        Assert.Equal(FakeSysAdminAuthRepository.ValidSessionToken, factory.AuthRepository.LastSessionToken);
        Assert.Equal("ops@example.com", factory.NotificationWorkflow.LastDestination);
        Assert.Equal(factory.AuthRepository.ValidationDisplayName, factory.NotificationWorkflow.LastRecipientDisplayName);
        Assert.True(payload!.Succeeded);
        Assert.Equal("smtp", payload.ProviderKey);
        Assert.Equal("ops@example.com", payload.Destination);
        Assert.True(payload.Readiness.IsVerificationDeliveryReady);
    }

    [Fact]
    public async Task SendNotificationTest_ReturnsBadRequest_WhenWorkflowRejectsRequest()
    {
        using var factory = new SysAdminNotificationApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, FakeSysAdminAuthRepository.ValidSessionToken);

        factory.NotificationWorkflow.OnSendTest = static (_, _, _) =>
            throw new InvalidOperationException("A destination email is required for notification test send.");

        var response = await client.PostAsJsonAsync(
            "/control/notification-readiness/test-send",
            new NotificationTestSendRequest
            {
                Destination = string.Empty,
                RecipientDisplayName = "Ops"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "A destination email is required for notification test send.",
            document.RootElement.GetProperty("message").GetString());
    }

    private sealed class SysAdminNotificationApiApplicationFactory : WebApplicationFactory<global::Program>
    {
        public FakePlatformRuntimeStateRepository RuntimeStateRepository { get; } = new();

        public FakeSysAdminAuthRepository AuthRepository { get; } = new();

        public FakePlatformFirstCompanyProvisioningRepository FirstCompanyProvisioningRepository { get; } = new();

        public FakePlatformNotificationReadinessWorkflow NotificationWorkflow { get; } = new();

        public FakePlatformGovernanceRepository GovernanceRepository { get; } = new();

        public FakeCompanyMembershipGovernanceWorkflow MembershipGovernanceWorkflow { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(
                (_, configurationBuilder) =>
                {
                    configurationBuilder.AddInMemoryCollection(
                    [
                        KeyValuePair.Create<string, string?>("ConnectionStrings:AccountingCore", "Host=127.0.0.1;Port=5432;Database=citus_tests;Username=postgres;Password=postgres"),
                        KeyValuePair.Create<string, string?>("SysAdminAuthentication:Bootstrap:Enabled", bool.FalseString)
                    ]);
                });
            builder.ConfigureServices(
                services =>
                {
                    services.RemoveAll<IPlatformRuntimeStateRepository>();
                    services.AddSingleton<IPlatformRuntimeStateRepository>(RuntimeStateRepository);
                    services.RemoveAll<ISysAdminAuthRepository>();
                    services.AddSingleton<ISysAdminAuthRepository>(AuthRepository);
                    services.RemoveAll<IPlatformFirstCompanyProvisioningRepository>();
                    services.AddSingleton<IPlatformFirstCompanyProvisioningRepository>(FirstCompanyProvisioningRepository);
                    services.RemoveAll<IPlatformGovernanceRepository>();
                    services.AddSingleton<IPlatformGovernanceRepository>(GovernanceRepository);
                    services.RemoveAll<ICompanyMembershipGovernanceWorkflow>();
                    services.AddSingleton<ICompanyMembershipGovernanceWorkflow>(MembershipGovernanceWorkflow);
                    services.RemoveAll<IPlatformNotificationReadinessWorkflow>();
                    services.AddSingleton<IPlatformNotificationReadinessWorkflow>(NotificationWorkflow);
                });
        }
    }

    private sealed class FakePlatformFirstCompanyProvisioningRepository : IPlatformFirstCompanyProvisioningRepository
    {
        public Func<PlatformFirstCompanyProvisioningCommand, CancellationToken, Task<PlatformFirstCompanyProvisioningResult>> OnProvision { get; set; } =
            static (_, _) => Task.FromResult(new PlatformFirstCompanyProvisioningResult { Succeeded = false, FailureCode = "not_configured", FailureMessage = "Provisioning behavior was not configured for this test." });

        public PlatformFirstCompanyProvisioningCommand? LastCommand { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlatformFirstCompanyProvisioningResult> ProvisionAsync(
            PlatformFirstCompanyProvisioningCommand command,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            return OnProvision(command, cancellationToken);
        }
    }

    private sealed class FakePlatformRuntimeStateRepository : IPlatformRuntimeStateRepository
    {
        public PlatformMaintenanceState? MaintenanceState { get; set; }

        public PlatformNotificationReadinessState? NotificationReadinessState { get; set; }

        public PlatformFirstCompanySetupState? FirstCompanySetupState { get; set; }

        public PlatformNotificationReadinessState? LastUpsertedNotificationReadinessState { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlatformMaintenanceState?> GetMaintenanceStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(MaintenanceState);

        public Task<PlatformMaintenanceState> UpsertMaintenanceStateAsync(
            PlatformMaintenanceState state,
            CancellationToken cancellationToken)
        {
            MaintenanceState = state;
            return Task.FromResult(state);
        }

        public Task<PlatformNotificationReadinessState?> GetNotificationReadinessStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NotificationReadinessState);

        public Task<PlatformNotificationReadinessState> UpsertNotificationReadinessStateAsync(
            PlatformNotificationReadinessState state,
            CancellationToken cancellationToken)
        {
            NotificationReadinessState = state;
            LastUpsertedNotificationReadinessState = state;
            return Task.FromResult(state);
        }

        public Task<PlatformFirstCompanySetupState?> GetFirstCompanySetupStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(FirstCompanySetupState);

        public Task<PlatformFirstCompanySetupState> UpsertFirstCompanySetupStateAsync(
            PlatformFirstCompanySetupState state,
            CancellationToken cancellationToken)
        {
            FirstCompanySetupState = state;
            return Task.FromResult(state);
        }
    }

    private sealed class FakeSysAdminAuthRepository : ISysAdminAuthRepository
    {
        public const string ValidSessionToken = "test-sysadmin-session";

        public static UserId ValidSysAdminAccountId { get; } = UserId.FromOrdinal(1);

        public string ValidationDisplayName { get; } = "Platform Operator";

        public string LastSessionToken { get; private set; } = string.Empty;

        public SysAdminSetupStatus SetupStatus { get; set; } = new()
        {
            AccountCount = 1,
            CompanyCount = 1,
            OwnerMembershipCount = 1
        };

        public Func<CancellationToken, Task<SysAdminSetupStatus>>? OnGetSetupStatus { get; set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SysAdminSetupStatus> GetSetupStatusAsync(CancellationToken cancellationToken) =>
            OnGetSetupStatus?.Invoke(cancellationToken) ?? Task.FromResult(SetupStatus);

        public Task EnsureBootstrapAccountAsync(
            string email,
            string password,
            string displayName,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<SysAdminFirstAccountProvisioningResult> ProvisionFirstAccountAsync(
            string email,
            string password,
            string displayName,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SysAdminFirstAccountProvisioningResult());

        public Task<SysAdminAuthenticationResult> AuthenticateAsync(
            string email,
            string password,
            TimeSpan sessionLifetime,
            string? remoteIp,
            string? userAgent,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SysAdminAuthenticationResult
            {
                Succeeded = true,
                SessionToken = ValidSessionToken,
                SysAdminAccountId = ValidSysAdminAccountId,
                Email = "sysadmin@example.com",
                DisplayName = ValidationDisplayName,
                Roles = ["platform-ops"],
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4)
            });

        public Task<SysAdminSessionValidationResult> ValidateSessionAsync(
            string sessionToken,
            CancellationToken cancellationToken)
        {
            LastSessionToken = sessionToken;

            if (!string.Equals(sessionToken, ValidSessionToken, StringComparison.Ordinal))
            {
                return Task.FromResult(new SysAdminSessionValidationResult
                {
                    Succeeded = false,
                    FailureCode = "invalid_session",
                    FailureMessage = "The SysAdmin session is invalid."
                });
            }

            return Task.FromResult(new SysAdminSessionValidationResult
            {
                Succeeded = true,
                SysAdminAccountId = ValidSysAdminAccountId,
                Email = "sysadmin@example.com",
                DisplayName = ValidationDisplayName,
                Roles = ["platform-ops"],
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4)
            });
        }

        public Task RevokeSessionAsync(
            string sessionToken,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<SysAdminSecretRotationResult> RotateSecretAsync(
            UserId sysAdminAccountId,
            string currentPassword,
            string newPassword,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SysAdminSecretRotationResult
            {
                Succeeded = true
            });
    }

    private sealed class FakePlatformGovernanceRepository : IPlatformGovernanceRepository
    {
        public Func<CancellationToken, Task<IReadOnlyList<ManagedPlatformAccountSummary>>> OnListManagedUsers { get; set; } =
            static _ => Task.FromResult<IReadOnlyList<ManagedPlatformAccountSummary>>(Array.Empty<ManagedPlatformAccountSummary>());

        public Func<int, CancellationToken, Task<IReadOnlyList<PlatformAuditEvent>>> OnListRecentAuditEvents { get; set; } =
            static (_, _) => Task.FromResult<IReadOnlyList<PlatformAuditEvent>>(Array.Empty<PlatformAuditEvent>());

        public Func<UserId, int, CancellationToken, Task<IReadOnlyList<PlatformAuditEvent>>> OnListAccountMfaTimeline { get; set; } =
            static (_, _, _) => Task.FromResult<IReadOnlyList<PlatformAuditEvent>>(Array.Empty<PlatformAuditEvent>());

        public Func<UserId, int, CancellationToken, Task<IReadOnlyList<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>>> OnListAccountMfaRecoveryHistory { get; set; } =
            static (_, _, _) => Task.FromResult<IReadOnlyList<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>>(Array.Empty<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>());

        public Func<CompanyId, string, string, UserId?, CancellationToken, Task<CompanyStatusGovernanceResult?>> OnSetCompanyStatus { get; set; } =
            static (_, _, _, _, _) => Task.FromResult<CompanyStatusGovernanceResult?>(null);

        public Func<UserId, string, DateTimeOffset?, string, UserId?, CancellationToken, Task<AccountStatusGovernanceResult?>> OnSetAccountStatus { get; set; } =
            static (_, _, _, _, _, _) => Task.FromResult<AccountStatusGovernanceResult?>(null);

        public Func<UserId, string, UserId?, CancellationToken, Task<PasswordResetGovernanceResult?>> OnRequestPasswordReset { get; set; } =
            static (_, _, _, _) => Task.FromResult<PasswordResetGovernanceResult?>(null);

        public Func<UserId, string, UserId?, CancellationToken, Task<AccountMfaResetGovernanceResult?>> OnResetAccountMfa { get; set; } =
            static (_, _, _, _) => Task.FromResult<AccountMfaResetGovernanceResult?>(null);

        public Func<CancellationToken, Task<IReadOnlyList<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>>> OnListOpenMfaRecoveryRequests { get; set; } =
            static _ => Task.FromResult<IReadOnlyList<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>>(Array.Empty<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>());

        public Func<Guid, string, string, UserId?, CancellationToken, Task<MfaRecoveryReviewResult?>> OnReviewMfaRecoveryRequest { get; set; } =
            static (_, _, _, _, _) => Task.FromResult<MfaRecoveryReviewResult?>(null);

        public Func<Guid, string, UserId?, CancellationToken, Task<MfaRecoveryExecutionResult?>> OnExecuteMfaRecoveryRequest { get; set; } =
            static (_, _, _, _) => Task.FromResult<MfaRecoveryExecutionResult?>(null);

        public int? LastAuditLimit { get; private set; }

        public UserId? LastMfaTimelineAccountId { get; private set; }

        public int? LastMfaTimelineLimit { get; private set; }

        public UserId? LastMfaRecoveryHistoryAccountId { get; private set; }

        public int? LastMfaRecoveryHistoryLimit { get; private set; }

        public CompanyId? LastCompanyStatusCompanyId { get; private set; }

        public string LastCompanyStatusStatus { get; private set; } = string.Empty;

        public string LastCompanyStatusReason { get; private set; } = string.Empty;

        public UserId? LastCompanyStatusSysAdminAccountId { get; private set; }

        public UserId? LastAccountStatusAccountId { get; private set; }

        public string LastAccountStatusStatus { get; private set; } = string.Empty;

        public DateTimeOffset? LastAccountStatusLockedUntilUtc { get; private set; }

        public string LastAccountStatusReason { get; private set; } = string.Empty;

        public UserId? LastAccountStatusSysAdminAccountId { get; private set; }

        public UserId? LastPasswordResetAccountId { get; private set; }

        public string LastPasswordResetReason { get; private set; } = string.Empty;

        public UserId? LastPasswordResetSysAdminAccountId { get; private set; }

        public UserId? LastMfaResetAccountId { get; private set; }

        public string LastMfaResetReason { get; private set; } = string.Empty;

        public UserId? LastMfaResetSysAdminAccountId { get; private set; }

        public Guid? LastReviewedMfaRecoveryRequestId { get; private set; }

        public string LastReviewedMfaRecoveryDecision { get; private set; } = string.Empty;

        public string LastReviewedMfaRecoveryReason { get; private set; } = string.Empty;

        public UserId? LastReviewedMfaRecoverySysAdminAccountId { get; private set; }

        public Guid? LastExecutedMfaRecoveryRequestId { get; private set; }

        public string LastExecutedMfaRecoveryReason { get; private set; } = string.Empty;

        public UserId? LastExecutedMfaRecoverySysAdminAccountId { get; private set; }

        public int ListManagedUsersCallCount { get; private set; }

        public int ListOpenMfaRecoveryRequestsCallCount { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ManagedPlatformAccountSummary>> ListManagedUsersAsync(CancellationToken cancellationToken)
        {
            ListManagedUsersCallCount++;
            return OnListManagedUsers(cancellationToken);
        }

        public Task<IReadOnlyList<PlatformAuditEvent>> ListRecentAuditEventsAsync(
            int limit,
            CancellationToken cancellationToken)
        {
            LastAuditLimit = limit;
            return OnListRecentAuditEvents(limit, cancellationToken);
        }

        public Task<IReadOnlyList<PlatformAuditEvent>> ListAccountMfaTimelineAsync(
            UserId accountId,
            int limit,
            CancellationToken cancellationToken)
        {
            LastMfaTimelineAccountId = accountId;
            LastMfaTimelineLimit = limit;
            return OnListAccountMfaTimeline(accountId, limit, cancellationToken);
        }

        public Task<IReadOnlyList<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>> ListAccountMfaRecoveryHistoryAsync(
            UserId accountId,
            int limit,
            CancellationToken cancellationToken)
        {
            LastMfaRecoveryHistoryAccountId = accountId;
            LastMfaRecoveryHistoryLimit = limit;
            return OnListAccountMfaRecoveryHistory(accountId, limit, cancellationToken);
        }

        public Task<CompanyStatusGovernanceResult?> SetCompanyStatusAsync(
            CompanyId companyId,
            string status,
            string reason,
            UserId? sysAdminAccountId,
            CancellationToken cancellationToken)
        {
            LastCompanyStatusCompanyId = companyId;
            LastCompanyStatusStatus = status;
            LastCompanyStatusReason = reason;
            LastCompanyStatusSysAdminAccountId = sysAdminAccountId;
            return OnSetCompanyStatus(companyId, status, reason, sysAdminAccountId, cancellationToken);
        }

        public Task<AccountStatusGovernanceResult?> SetAccountStatusAsync(
            UserId accountId,
            string status,
            DateTimeOffset? lockedUntilUtc,
            string reason,
            UserId? sysAdminAccountId,
            CancellationToken cancellationToken)
        {
            LastAccountStatusAccountId = accountId;
            LastAccountStatusStatus = status;
            LastAccountStatusLockedUntilUtc = lockedUntilUtc;
            LastAccountStatusReason = reason;
            LastAccountStatusSysAdminAccountId = sysAdminAccountId;
            return OnSetAccountStatus(accountId, status, lockedUntilUtc, reason, sysAdminAccountId, cancellationToken);
        }

        public Task<PasswordResetGovernanceResult?> RequestPasswordResetAsync(
            UserId accountId,
            string reason,
            UserId? sysAdminAccountId,
            CancellationToken cancellationToken)
        {
            LastPasswordResetAccountId = accountId;
            LastPasswordResetReason = reason;
            LastPasswordResetSysAdminAccountId = sysAdminAccountId;
            return OnRequestPasswordReset(accountId, reason, sysAdminAccountId, cancellationToken);
        }

        public Task<AccountMfaResetGovernanceResult?> ResetAccountMfaAsync(
            UserId accountId,
            string reason,
            UserId? sysAdminAccountId,
            CancellationToken cancellationToken)
        {
            LastMfaResetAccountId = accountId;
            LastMfaResetReason = reason;
            LastMfaResetSysAdminAccountId = sysAdminAccountId;
            return OnResetAccountMfa(accountId, reason, sysAdminAccountId, cancellationToken);
        }

        public Task<IReadOnlyList<Citus.Platform.Core.Runtime.MfaRecoveryRequestSummary>> ListOpenMfaRecoveryRequestsAsync(
            CancellationToken cancellationToken)
        {
            ListOpenMfaRecoveryRequestsCallCount++;
            return OnListOpenMfaRecoveryRequests(cancellationToken);
        }

        public Task<MfaRecoveryReviewResult?> ReviewMfaRecoveryRequestAsync(
            Guid requestId,
            string decision,
            string reason,
            UserId? sysAdminAccountId,
            CancellationToken cancellationToken)
        {
            LastReviewedMfaRecoveryRequestId = requestId;
            LastReviewedMfaRecoveryDecision = decision;
            LastReviewedMfaRecoveryReason = reason;
            LastReviewedMfaRecoverySysAdminAccountId = sysAdminAccountId;
            return OnReviewMfaRecoveryRequest(requestId, decision, reason, sysAdminAccountId, cancellationToken);
        }

        public Task<MfaRecoveryExecutionResult?> ExecuteMfaRecoveryRequestAsync(
            Guid requestId,
            string reason,
            UserId? sysAdminAccountId,
            CancellationToken cancellationToken)
        {
            LastExecutedMfaRecoveryRequestId = requestId;
            LastExecutedMfaRecoveryReason = reason;
            LastExecutedMfaRecoverySysAdminAccountId = sysAdminAccountId;
            return OnExecuteMfaRecoveryRequest(requestId, reason, sysAdminAccountId, cancellationToken);
        }
    }

    private sealed class FakeCompanyMembershipGovernanceWorkflow : ICompanyMembershipGovernanceWorkflow
    {
        public Func<CompanyId, Guid, string, string, UserId?, CancellationToken, Task<CompanyMembershipRoleChangeResult>> OnChangeRole { get; set; } =
            static (companyId, membershipId, role, reason, _, _) =>
                Task.FromResult(
                    new CompanyMembershipRoleChangeResult
                    {
                        CompanyId = companyId,
                        MembershipId = membershipId,
                        AccountId = default,
                        Email = string.Empty,
                        Username = string.Empty,
                        PreviousRole = "user",
                        Role = role,
                        Reason = reason,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });

        public CompanyId? LastCompanyId { get; private set; }

        public Guid? LastMembershipId { get; private set; }

        public string LastRole { get; private set; } = string.Empty;

        public string LastReason { get; private set; } = string.Empty;

        public UserId? LastSysAdminAccountId { get; private set; }

        public Task<CompanyMembershipRoleChangeResult> ChangeRoleFromSysAdminAsync(
            CompanyId companyId,
            Guid membershipId,
            string role,
            string reason,
            UserId? sysAdminAccountId,
            CancellationToken cancellationToken)
        {
            LastCompanyId = companyId;
            LastMembershipId = membershipId;
            LastRole = role;
            LastReason = reason;
            LastSysAdminAccountId = sysAdminAccountId;
            return OnChangeRole(companyId, membershipId, role, reason, sysAdminAccountId, cancellationToken);
        }
    }

    private sealed class FakePlatformNotificationReadinessWorkflow : IPlatformNotificationReadinessWorkflow
    {
        public Func<CancellationToken, Task<PlatformNotificationReadinessReport>> OnGet { get; set; } =
            static _ => Task.FromResult(new PlatformNotificationReadinessReport());

        public Func<string, string, CancellationToken, Task<PlatformNotificationTestSendResult>> OnSendTest { get; set; } =
            static (destination, _, _) => Task.FromResult(
                new PlatformNotificationTestSendResult
                {
                    Succeeded = true,
                    ProviderKey = "smtp",
                    Destination = destination,
                    Readiness = new PlatformNotificationReadinessReport()
                });

        public int SendTestCallCount { get; private set; }

        public int GetCallCount { get; private set; }

        public string LastDestination { get; private set; } = string.Empty;

        public string LastRecipientDisplayName { get; private set; } = string.Empty;

        public Task<PlatformNotificationReadinessReport> GetAsync(CancellationToken cancellationToken)
        {
            GetCallCount++;
            return OnGet(cancellationToken);
        }

        public Task<PlatformNotificationTestSendResult> SendTestAsync(
            string destination,
            string recipientDisplayName,
            CancellationToken cancellationToken)
        {
            SendTestCallCount++;
            LastDestination = destination;
            LastRecipientDisplayName = recipientDisplayName;
            return OnSendTest(destination, recipientDisplayName, cancellationToken);
        }
    }
}
