using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Control;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Citus.Business.Blazor.Tests;

public sealed class ProfileApiContractTests
{
    [Fact]
    public async Task GetProfile_ReturnsUnauthorized_WhenBusinessSessionHeaderMissing()
    {
        using var factory = new ProfileApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/platform/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.Workflow.LastUserId);
        Assert.Null(factory.BusinessSessions.LastValidatedSessionToken);
    }

    [Fact]
    public async Task GetProfile_ReturnsSummary_ForAuthenticatedBusinessSession()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("71428232-81ae-4f76-87cb-bdc7fd6bbc16");
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnGet = static (actorUserId, _) =>
            Task.FromResult<PlatformAccountProfileSummary?>(
                CreateProfile(
                    actorUserId,
                    displayName: "Morgan Hale",
                    lastMfaResetAtUtc: new DateTimeOffset(2026, 4, 16, 23, 45, 0, TimeSpan.Zero),
                    lastMfaResetReason: "Operator recovery reset",
                    lastMfaResetByDisplayName: "Platform Operator"));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-21");

        var response = await client.GetAsync("/api/platform/profile");

        response.EnsureSuccessStatusCode();

        var profile = await response.Content.ReadFromJsonAsync<PlatformAccountProfileSummary>();

        Assert.NotNull(profile);
        Assert.Equal("BUSINESS-TOKEN-21", factory.BusinessSessions.LastValidatedSessionToken);
        Assert.Equal(userId, factory.Workflow.LastUserId);
        Assert.Equal(userId, profile!.UserId);
        Assert.Equal("Morgan Hale", profile.DisplayName);
        Assert.Equal("Operator recovery reset", profile.LastMfaResetReason);
        Assert.Equal("Platform Operator", profile.LastMfaResetByDisplayName);
    }

    [Fact]
    public async Task GetProfile_ExposesRecoveryPolicyState_WhenRecoveryIsOpen()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("4ca619ff-cdd0-4f69-a3d7-0113447c9299");
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnGet = static (actorUserId, _) =>
            Task.FromResult<PlatformAccountProfileSummary?>(
                CreateProfile(
                    actorUserId,
                    mfaMode: "totp_app",
                    activeMfaRecoveryRequestId: Guid.Parse("ee924ff1-005f-4d1f-987c-a12996b0b85f"),
                    activeMfaRecoveryStatus: "approved"));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-21B");

        var response = await client.GetAsync("/api/platform/profile");

        response.EnsureSuccessStatusCode();

        var profile = await response.Content.ReadFromJsonAsync<PlatformAccountProfileSummary>();

        Assert.NotNull(profile);
        Assert.True(profile!.HasActiveMfaRecoveryRequest);
        Assert.False(profile.CanRequestMfaRecovery);
        Assert.True(profile.IsSysAdminEmergencyMfaResetBlocked);
        Assert.Contains("already open", profile.MfaRecoveryPolicyReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocked", profile.EmergencyMfaResetPolicyReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveDisplayName_ReturnsUpdatedSummary_ForAuthenticatedBusinessSession()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("2bcde47f-329d-491c-9d85-4169bff7af96");
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnSaveDisplayName = static (actorUserId, displayName, _) =>
            Task.FromResult<PlatformAccountProfileSummary?>(CreateProfile(actorUserId, displayName: displayName));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-22");

        var response = await client.PutAsJsonAsync(
            "/api/platform/profile/display-name",
            new
            {
                displayName = "Morgan Hale"
            });

        response.EnsureSuccessStatusCode();

        var profile = await response.Content.ReadFromJsonAsync<PlatformAccountProfileSummary>();

        Assert.NotNull(profile);
        Assert.Equal(userId, factory.Workflow.LastUserId);
        Assert.Equal("Morgan Hale", factory.Workflow.LastDisplayName);
        Assert.Equal("Morgan Hale", profile!.DisplayName);
    }

    [Fact]
    public async Task SaveMfaMode_ReturnsUpdatedSummary_ForAuthenticatedBusinessSession()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("6ac137e3-1230-417f-b2d3-57f9d8c0409b");
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnSaveMfaMode = static (actorUserId, mfaMode, _) =>
            Task.FromResult<PlatformAccountProfileSummary?>(CreateProfile(actorUserId, mfaMode: mfaMode));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-22A");

        var response = await client.PutAsJsonAsync(
            "/api/platform/profile/mfa-mode",
            new
            {
                mfaMode = "email_code"
            });

        response.EnsureSuccessStatusCode();

        var profile = await response.Content.ReadFromJsonAsync<PlatformAccountProfileSummary>();

        Assert.NotNull(profile);
        Assert.Equal(userId, factory.Workflow.LastUserId);
        Assert.Equal("email_code", factory.Workflow.LastMfaMode);
        Assert.Equal("email_code", profile!.MfaMode);
    }

    [Fact]
    public async Task BeginTotpEnrollment_ReturnsBootstrapPayload_WhenAuthenticated()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("089ee8c1-a3ea-496d-bbc8-3f4ef2c60a52");
        var enrollmentId = Guid.Parse("101d8b17-7521-468f-a40e-bb9792d5f4c5");
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnBeginTotpEnrollment = static (actorUserId, _) =>
            Task.FromResult<PlatformTotpEnrollmentStartResult?>(
                new()
                {
                    EnrollmentId = Guid.Parse("101d8b17-7521-468f-a40e-bb9792d5f4c5"),
                    Issuer = "Citus",
                    AccountLabel = "taylor.rowan@example.com",
                    SecretBase32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567",
                    OtpAuthUri = "otpauth://totp/Citus:taylor.rowan@example.com",
                    ExpiresAtUtc = new DateTimeOffset(2026, 4, 17, 1, 0, 0, TimeSpan.Zero),
                    Profile = CreateProfile(actorUserId)
                });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-22T");

        var response = await client.PostAsync("/api/platform/profile/mfa/totp-enrollment/start", null);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<PlatformTotpEnrollmentStartResult>();

        Assert.NotNull(payload);
        Assert.Equal(userId, factory.Workflow.LastUserId);
        Assert.Equal(enrollmentId, payload!.EnrollmentId);
        Assert.Equal("totp_app", payload.Factor);
    }

    [Fact]
    public async Task ConfirmTotpEnrollment_ReturnsConfirmationPayload_WhenAuthenticated()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("0174cdb8-4057-412f-aa24-a57d72bfb5d6");
        var enrollmentId = Guid.Parse("16fc4ac6-36ec-460d-ad92-d9b0e7fdc9fe");
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnConfirmTotpEnrollment = static (actorUserId, requestedEnrollmentId, verificationCode, _) =>
            Task.FromResult<PlatformTotpEnrollmentConfirmationResult?>(
                new()
                {
                    EnrollmentId = requestedEnrollmentId,
                    ConfirmedAtUtc = new DateTimeOffset(2026, 4, 17, 1, 15, 0, TimeSpan.Zero),
                    Profile = CreateProfile(actorUserId, mfaMode: "totp_app", previousMfaMode: "email_code")
                });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-22U");

        var response = await client.PostAsJsonAsync(
            "/api/platform/profile/mfa/totp-enrollment/confirm",
            new
            {
                enrollmentId,
                verificationCode = "123456"
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<PlatformTotpEnrollmentConfirmationResult>();

        Assert.NotNull(payload);
        Assert.Equal(userId, factory.Workflow.LastUserId);
        Assert.Equal(enrollmentId, factory.Workflow.LastTotpEnrollmentId);
        Assert.Equal("123456", factory.Workflow.LastTotpVerificationCode);
        Assert.Equal("totp_app", payload!.Profile.MfaMode);
    }

    [Fact]
    public async Task RequestMfaRecovery_ReturnsRequestPayload_WhenAuthenticated()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("dc444247-6a04-4546-b4eb-3add9921eb2c");
        var requestedAt = new DateTimeOffset(2026, 4, 16, 21, 45, 0, TimeSpan.Zero);
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnRequestMfaRecovery = static (actorUserId, reason, _) =>
            Task.FromResult<PlatformMfaRecoveryRequestResult?>(
                new()
                {
                    RequestId = Guid.Parse("3693cc53-83ea-4958-9c1f-fd445cb181e5"),
                    Status = "requested",
                    Reason = reason,
                    RequestedAtUtc = new DateTimeOffset(2026, 4, 16, 21, 45, 0, TimeSpan.Zero),
                    Profile = CreateProfile(actorUserId, mfaMode: "email_code")
                });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-22B");

        var response = await client.PostAsJsonAsync(
            "/api/platform/profile/mfa-recovery/request",
            new
            {
                reason = "Lost access to the mailbox device used for MFA."
            });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<PlatformMfaRecoveryRequestResult>();

        Assert.NotNull(payload);
        Assert.Equal(userId, factory.Workflow.LastUserId);
        Assert.Equal("Lost access to the mailbox device used for MFA.", factory.Workflow.LastMfaRecoveryReason);
        Assert.Equal("requested", payload!.Status);
        Assert.Equal(requestedAt, payload.RequestedAtUtc);
    }

    [Fact]
    public async Task RequestEmailChange_ReturnsRequestPayload_WhenAuthenticated()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("cb0d8d92-830a-4054-8f76-73a08dfe52e4");
        var expiresAt = new DateTimeOffset(2026, 4, 16, 19, 25, 0, TimeSpan.Zero);
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnRequestEmailChange = static (actorUserId, newEmail, _) =>
            Task.FromResult<PlatformProfileChangeRequestResult?>(
                new()
                {
                    ChangeType = "email_change",
                    MaskedDestination = $"masked:{newEmail}",
                    ExpiresAtUtc = new DateTimeOffset(2026, 4, 16, 19, 25, 0, TimeSpan.Zero),
                    Profile = CreateProfile(actorUserId, email: newEmail)
                });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-23");

        var response = await client.PostAsJsonAsync(
            "/api/platform/profile/email-change/request",
            new
            {
                newEmail = "new.operator@example.com"
            });

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PlatformProfileChangeRequestResult>();

        Assert.NotNull(result);
        Assert.Equal(userId, factory.Workflow.LastUserId);
        Assert.Equal("new.operator@example.com", factory.Workflow.LastEmail);
        Assert.Equal("email_change", result!.ChangeType);
        Assert.Equal("masked:new.operator@example.com", result.MaskedDestination);
        Assert.Equal(expiresAt, result.ExpiresAtUtc);
    }

    [Fact]
    public async Task ConfirmEmailChange_ReturnsNotFound_WhenWorkflowReturnsNull()
    {
        using var factory = new ProfileApiApplicationFactory();
        factory.BusinessSessions.ValidateResult = CreateSession(Guid.Parse("f8c308b4-90c7-4380-ba51-cd5a9eb3e1d0"));
        factory.Workflow.OnConfirmEmailChange = static (_, _, _) =>
            Task.FromResult<PlatformProfileChangeConfirmationResult?>(null);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-24");

        var response = await client.PostAsJsonAsync(
            "/api/platform/profile/email-change/confirm",
            new
            {
                verificationCode = "AB12CD"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RequestPasswordChange_ReturnsBadRequest_WhenWorkflowRejectsRequest()
    {
        using var factory = new ProfileApiApplicationFactory();
        factory.BusinessSessions.ValidateResult = CreateSession(Guid.Parse("0b4880d6-7dd2-4100-af1f-f2cc7b6f1f7f"));
        factory.Workflow.OnRequestPasswordChange = static (_, _, _) =>
            throw new InvalidOperationException("Verification delivery is currently blocked.");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-25");

        var response = await client.PostAsJsonAsync(
            "/api/platform/profile/password-change/request",
            new
            {
                newPassword = "Sup3rSecret!"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Verification delivery is currently blocked.",
            document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConfirmPasswordChange_ReturnsConfirmationPayload()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("fc32d1a2-aea6-4ac1-bb17-0e54caef6d44");
        var confirmedAt = new DateTimeOffset(2026, 4, 16, 19, 40, 0, TimeSpan.Zero);
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnConfirmPasswordChange = static (actorUserId, verificationCode, _) =>
            Task.FromResult<PlatformProfileChangeConfirmationResult?>(
                new()
                {
                    ChangeType = "password_change",
                    ConfirmedAtUtc = new DateTimeOffset(2026, 4, 16, 19, 40, 0, TimeSpan.Zero),
                    Profile = CreateProfile(actorUserId, displayName: $"confirmed:{verificationCode}")
                });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-26");

        var response = await client.PostAsJsonAsync(
            "/api/platform/profile/password-change/confirm",
            new
            {
                verificationCode = "ZX90QP"
            });

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PlatformProfileChangeConfirmationResult>();

        Assert.NotNull(result);
        Assert.Equal(userId, factory.Workflow.LastUserId);
        Assert.Equal("ZX90QP", factory.Workflow.LastVerificationCode);
        Assert.Equal("password_change", result!.ChangeType);
        Assert.Equal(confirmedAt, result.ConfirmedAtUtc);
        Assert.Equal("confirmed:ZX90QP", result.Profile.DisplayName);
    }

    [Fact]
    public async Task GetProfile_ReturnsUnauthorized_WhenBusinessSessionIsInvalid()
    {
        using var factory = new ProfileApiApplicationFactory();
        factory.BusinessSessions.ValidateResult = new PlatformBusinessSessionResult
        {
            Succeeded = false,
            FailureCode = "session_invalid",
            FailureMessage = "Session could not be validated."
        };

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-27");

        var response = await client.GetAsync("/api/platform/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.Workflow.LastUserId);
    }

    [Fact]
    public async Task GetNotificationReadiness_ReturnsUnauthorized_WhenBusinessSessionHeaderMissing()
    {
        using var factory = new ProfileApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/platform/notification-readiness");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMfaTimeline_ReturnsTimelineEntries_WhenAuthenticated()
    {
        using var factory = new ProfileApiApplicationFactory();
        var userId = Guid.Parse("f9eb43b4-12fb-4843-b46e-c3246ca994fc");
        factory.BusinessSessions.ValidateResult = CreateSession(userId);
        factory.Workflow.OnGetMfaTimeline = static (actorUserId, _) =>
            Task.FromResult<IReadOnlyList<PlatformMfaTimelineEntry>>(
            [
                new PlatformMfaTimelineEntry
                {
                    Action = "account_mfa_recovery_requested",
                    ActionLabel = "Account MFA Recovery Requested",
                    Detail = "email_code | requested",
                    Reason = "Lost access to verified mailbox device.",
                    ActorType = "user",
                    ActorDisplayName = $"actor:{actorUserId}",
                    CreatedAtUtc = new DateTimeOffset(2026, 4, 16, 22, 15, 0, TimeSpan.Zero)
                }
            ]);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-28A");

        var response = await client.GetAsync("/api/platform/profile/mfa-timeline");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<PlatformMfaTimelineEntry>>();

        Assert.NotNull(payload);
        Assert.Single(payload!);
        Assert.Equal(userId, factory.Workflow.LastUserId);
        Assert.Equal("account_mfa_recovery_requested", payload[0].Action);
        Assert.Equal("Lost access to verified mailbox device.", payload[0].Reason);
    }

    [Fact]
    public async Task GetNotificationReadiness_ReturnsReadinessSummary_WhenAuthenticated()
    {
        using var factory = new ProfileApiApplicationFactory();
        factory.BusinessSessions.ValidateResult = CreateSession(Guid.Parse("3d7ec32b-ffde-40c0-93fa-196d9de3720e"));
        factory.NotificationWorkflow.OnGet = static _ =>
            Task.FromResult(
                new Citus.Platform.Core.Runtime.PlatformNotificationReadinessReport
                {
                    ConfigPresent = true,
                    TestStatus = "passed",
                    VerificationReady = true,
                    IsVerificationDeliveryReady = true,
                    BlockingReason = string.Empty,
                    ConfigurationError = string.Empty,
                    LastTestedAtUtc = new DateTimeOffset(2026, 4, 16, 20, 5, 0, TimeSpan.Zero)
                });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessAuthHeaderNames.SessionToken, "BUSINESS-TOKEN-28");

        var response = await client.GetAsync("/api/platform/notification-readiness");

        response.EnsureSuccessStatusCode();

        var readiness = await response.Content.ReadFromJsonAsync<NotificationReadinessSummary>();

        Assert.NotNull(readiness);
        Assert.True(readiness!.IsVerificationDeliveryReady);
        Assert.Equal("passed", readiness.TestStatus);
    }

    private static PlatformBusinessSessionResult CreateSession(Guid userId) =>
        new()
        {
            Succeeded = true,
            SessionToken = "IGNORED",
            UserId = userId,
            ActiveCompanyId = Guid.Parse("0bf1e115-0437-44eb-b3be-11ec2358a925"),
            ExpiresAtUtc = new DateTimeOffset(2026, 4, 17, 4, 0, 0, TimeSpan.Zero)
        };

    private static PlatformAccountProfileSummary CreateProfile(
        Guid userId,
        string displayName = "Taylor Rowan",
        string email = "taylor.rowan@example.com",
        string mfaMode = "none",
        DateTimeOffset? lastMfaModeChangedAtUtc = null,
        string previousMfaMode = "",
        DateTimeOffset? lastMfaResetAtUtc = null,
        string lastMfaResetReason = "",
        string lastMfaResetByDisplayName = "",
        Guid? activeMfaRecoveryRequestId = null,
        string activeMfaRecoveryStatus = "") =>
        new()
        {
            UserId = userId,
            Username = "taylor.rowan",
            DisplayName = displayName,
            Email = email,
            Status = "active",
            EmailVerifiedAtUtc = new DateTimeOffset(2026, 4, 10, 8, 0, 0, TimeSpan.Zero),
            MfaMode = mfaMode,
            LastMfaModeChangedAtUtc = lastMfaModeChangedAtUtc,
            PreviousMfaMode = previousMfaMode,
            LastMfaResetAtUtc = lastMfaResetAtUtc,
            LastMfaResetReason = lastMfaResetReason,
            LastMfaResetByDisplayName = lastMfaResetByDisplayName,
            ActiveMfaRecoveryRequestId = activeMfaRecoveryRequestId,
            ActiveMfaRecoveryStatus = activeMfaRecoveryStatus,
            NotificationVerificationReady = true,
            NotificationBlockingReason = string.Empty
        };

    private sealed class ProfileApiApplicationFactory : WebApplicationFactory<global::Web.Shell.App>
    {
        public FakePlatformAccountProfileWorkflow Workflow { get; } = new();

        public FakePlatformNotificationReadinessWorkflow NotificationWorkflow { get; } = new();

        public FakePlatformBusinessSessionRepository BusinessSessions { get; } = new();

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
                    services.RemoveAll<IPlatformAccountProfileWorkflow>();
                    services.AddSingleton<IPlatformAccountProfileWorkflow>(Workflow);
                    services.RemoveAll<Citus.Platform.Core.Services.IPlatformNotificationReadinessWorkflow>();
                    services.AddSingleton<Citus.Platform.Core.Services.IPlatformNotificationReadinessWorkflow>(NotificationWorkflow);
                    services.RemoveAll<IPlatformBusinessSessionRepository>();
                    services.AddSingleton<IPlatformBusinessSessionRepository>(BusinessSessions);
                });
        }
    }

    private sealed class FakePlatformAccountProfileWorkflow : IPlatformAccountProfileWorkflow
    {
        public Func<Guid, CancellationToken, Task<PlatformAccountProfileSummary?>> OnGet { get; set; } =
            static (_, _) => Task.FromResult<PlatformAccountProfileSummary?>(null);

        public Func<Guid, CancellationToken, Task<IReadOnlyList<PlatformMfaTimelineEntry>>> OnGetMfaTimeline { get; set; } =
            static (_, _) => Task.FromResult<IReadOnlyList<PlatformMfaTimelineEntry>>(Array.Empty<PlatformMfaTimelineEntry>());

        public Func<Guid, CancellationToken, Task<PlatformTotpEnrollmentStartResult?>> OnBeginTotpEnrollment { get; set; } =
            static (_, _) => Task.FromResult<PlatformTotpEnrollmentStartResult?>(null);

        public Func<Guid, Guid, string, CancellationToken, Task<PlatformTotpEnrollmentConfirmationResult?>> OnConfirmTotpEnrollment { get; set; } =
            static (_, _, _, _) => Task.FromResult<PlatformTotpEnrollmentConfirmationResult?>(null);

        public Func<Guid, string, CancellationToken, Task<PlatformAccountProfileSummary?>> OnSaveDisplayName { get; set; } =
            static (_, _, _) => Task.FromResult<PlatformAccountProfileSummary?>(null);

        public Func<Guid, string, CancellationToken, Task<PlatformAccountProfileSummary?>> OnSaveMfaMode { get; set; } =
            static (_, _, _) => Task.FromResult<PlatformAccountProfileSummary?>(null);

        public Func<Guid, string, CancellationToken, Task<PlatformProfileChangeRequestResult?>> OnRequestEmailChange { get; set; } =
            static (_, _, _) => Task.FromResult<PlatformProfileChangeRequestResult?>(null);

        public Func<Guid, string, CancellationToken, Task<PlatformMfaRecoveryRequestResult?>> OnRequestMfaRecovery { get; set; } =
            static (_, _, _) => Task.FromResult<PlatformMfaRecoveryRequestResult?>(null);

        public Func<Guid, string, CancellationToken, Task<PlatformProfileChangeRequestResult?>> OnRequestPasswordChange { get; set; } =
            static (_, _, _) => Task.FromResult<PlatformProfileChangeRequestResult?>(null);

        public Func<Guid, string, CancellationToken, Task<PlatformProfileChangeConfirmationResult?>> OnConfirmEmailChange { get; set; } =
            static (_, _, _) => Task.FromResult<PlatformProfileChangeConfirmationResult?>(null);

        public Func<Guid, string, CancellationToken, Task<PlatformProfileChangeConfirmationResult?>> OnConfirmPasswordChange { get; set; } =
            static (_, _, _) => Task.FromResult<PlatformProfileChangeConfirmationResult?>(null);

        public Guid? LastUserId { get; private set; }

        public string? LastDisplayName { get; private set; }

        public string? LastMfaMode { get; private set; }

        public string? LastEmail { get; private set; }

        public string? LastMfaRecoveryReason { get; private set; }

        public string? LastPassword { get; private set; }

        public string? LastVerificationCode { get; private set; }

        public Guid? LastTotpEnrollmentId { get; private set; }

        public string? LastTotpVerificationCode { get; private set; }

        public async Task<PlatformAccountProfileSummary?> GetAsync(Guid userId, CancellationToken cancellationToken)
        {
            LastUserId = userId;
            return await OnGet(userId, cancellationToken);
        }

        public async Task<IReadOnlyList<PlatformMfaTimelineEntry>> GetMfaTimelineAsync(Guid userId, CancellationToken cancellationToken)
        {
            LastUserId = userId;
            return await OnGetMfaTimeline(userId, cancellationToken);
        }

        public async Task<PlatformTotpEnrollmentStartResult?> BeginTotpEnrollmentAsync(Guid userId, CancellationToken cancellationToken)
        {
            LastUserId = userId;
            return await OnBeginTotpEnrollment(userId, cancellationToken);
        }

        public async Task<PlatformTotpEnrollmentConfirmationResult?> ConfirmTotpEnrollmentAsync(
            Guid userId,
            Guid enrollmentId,
            string verificationCode,
            CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastTotpEnrollmentId = enrollmentId;
            LastTotpVerificationCode = verificationCode;
            return await OnConfirmTotpEnrollment(userId, enrollmentId, verificationCode, cancellationToken);
        }

        public async Task<PlatformAccountProfileSummary?> SaveDisplayNameAsync(
            Guid userId,
            string displayName,
            CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastDisplayName = displayName;
            return await OnSaveDisplayName(userId, displayName, cancellationToken);
        }

        public async Task<PlatformAccountProfileSummary?> SaveMfaModeAsync(
            Guid userId,
            string mfaMode,
            CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastMfaMode = mfaMode;
            return await OnSaveMfaMode(userId, mfaMode, cancellationToken);
        }

        public async Task<PlatformProfileChangeRequestResult?> RequestEmailChangeAsync(
            Guid userId,
            string newEmail,
            CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastEmail = newEmail;
            return await OnRequestEmailChange(userId, newEmail, cancellationToken);
        }

        public async Task<PlatformMfaRecoveryRequestResult?> RequestMfaRecoveryAsync(
            Guid userId,
            string reason,
            CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastMfaRecoveryReason = reason;
            return await OnRequestMfaRecovery(userId, reason, cancellationToken);
        }

        public async Task<PlatformProfileChangeRequestResult?> RequestPasswordChangeAsync(
            Guid userId,
            string newPassword,
            CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastPassword = newPassword;
            return await OnRequestPasswordChange(userId, newPassword, cancellationToken);
        }

        public async Task<PlatformProfileChangeConfirmationResult?> ConfirmEmailChangeAsync(
            Guid userId,
            string verificationCode,
            CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastVerificationCode = verificationCode;
            return await OnConfirmEmailChange(userId, verificationCode, cancellationToken);
        }

        public async Task<PlatformProfileChangeConfirmationResult?> ConfirmPasswordChangeAsync(
            Guid userId,
            string verificationCode,
            CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastVerificationCode = verificationCode;
            return await OnConfirmPasswordChange(userId, verificationCode, cancellationToken);
        }
    }

    private sealed class FakePlatformNotificationReadinessWorkflow : Citus.Platform.Core.Services.IPlatformNotificationReadinessWorkflow
    {
        public Func<CancellationToken, Task<Citus.Platform.Core.Runtime.PlatformNotificationReadinessReport>> OnGet { get; set; } =
            static _ => Task.FromResult(new Citus.Platform.Core.Runtime.PlatformNotificationReadinessReport());

        public Func<string, string, CancellationToken, Task<Citus.Platform.Core.Runtime.PlatformNotificationTestSendResult>> OnSendTest { get; set; } =
            static (destination, _, _) => Task.FromResult(
                new Citus.Platform.Core.Runtime.PlatformNotificationTestSendResult
                {
                    Succeeded = true,
                    Destination = destination,
                    Readiness = new Citus.Platform.Core.Runtime.PlatformNotificationReadinessReport()
                });

        public Task<Citus.Platform.Core.Runtime.PlatformNotificationReadinessReport> GetAsync(CancellationToken cancellationToken) =>
            OnGet(cancellationToken);

        public Task<Citus.Platform.Core.Runtime.PlatformNotificationTestSendResult> SendTestAsync(
            string destination,
            string recipientDisplayName,
            CancellationToken cancellationToken) =>
            OnSendTest(destination, recipientDisplayName, cancellationToken);
    }

    private sealed class FakePlatformBusinessSessionRepository : IPlatformBusinessSessionRepository
    {
        public PlatformBusinessSessionResult AuthenticateResult { get; set; } = new();

        public PlatformBusinessSessionResult CompleteSecondFactorResult { get; set; } = new();

        public PlatformBusinessSessionResult ValidateResult { get; set; } = new();

        public PlatformBusinessSessionResult SwitchResult { get; set; } = new();

        public string? LastValidatedSessionToken { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlatformBusinessSessionResult> AuthenticateAsync(
            string login,
            string password,
            TimeSpan sessionLifetime,
            string? remoteIp,
            string? userAgent,
            CancellationToken cancellationToken) =>
            Task.FromResult(AuthenticateResult);

        public Task<PlatformBusinessSessionResult> CompleteSecondFactorAsync(
            Guid challengeId,
            string verificationCode,
            TimeSpan sessionLifetime,
            string? remoteIp,
            string? userAgent,
            CancellationToken cancellationToken) =>
            Task.FromResult(CompleteSecondFactorResult);

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
            CancellationToken cancellationToken) =>
            Task.FromResult(SwitchResult);

        public Task RevokeSessionAsync(string sessionToken, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
