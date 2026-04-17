using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Modules;
using Citus.Platform.Core.Runtime;
using Citus.Platform.Core.Services;
using Citus.Platform.Infrastructure.Notifications;
using Citus.Platform.Infrastructure.Persistence;
using Citus.SysAdmin.Api.Control;
using Citus.SysAdmin.Api.Auth;
using Citus.SysAdmin.Api;
using Citus.Ui.Shared.Control;
using Citus.Ui.Shared.Shell;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.CompanyAccess;
using Microsoft.Extensions.Options;
using Modules.CompanyAccess.Memberships;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("AccountingCore") ??
    builder.Configuration["CITUS_ACCOUNTING_DB"];

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "A PostgreSQL connection string is required. Configure ConnectionStrings:AccountingCore or CITUS_ACCOUNTING_DB.");
}

builder.Services.AddSingleton(new PlatformPostgresConnectionFactory(connectionString));
builder.Services.AddSingleton(new PostgreSqlConnectionFactory(connectionString));
builder.Services.AddSingleton<SysAdminPasswordHasher>();
builder.Services.Configure<PlatformEmailDeliveryOptions>(builder.Configuration.GetSection(PlatformEmailDeliveryOptions.SectionName));
builder.Services.AddScoped<IPlatformMetadataRepository, PostgresPlatformMetadataRepository>();
builder.Services.AddScoped<IPlatformMetadataService, PlatformMetadataService>();
builder.Services.AddScoped<IPlatformBootstrapper, PlatformCoreBootstrapper>();
builder.Services.AddScoped<IPlatformGovernanceRepository, PostgresPlatformGovernanceRepository>();
builder.Services.AddSingleton<IPlatformVerificationNotificationSender, SmtpPlatformVerificationNotificationSender>();
builder.Services.AddSingleton<IPlatformNotificationReadinessWorkflow, PlatformNotificationReadinessWorkflow>();
builder.Services.AddScoped<ISysAdminAuthRepository, PostgresSysAdminAuthRepository>();
builder.Services.AddScoped<ICompanyMembershipPermissionStore, PostgreSqlCompanyMembershipPermissionStore>();
builder.Services.AddScoped<ICompanyMembershipGovernanceStore, PostgreSqlCompanyMembershipGovernanceStore>();
builder.Services.AddScoped<ICompanyMembershipGovernanceWorkflow, CompanyMembershipGovernanceWorkflow>();
builder.Services.AddSingleton<IPlatformRuntimeStateRepository, PostgresPlatformRuntimeStateRepository>();
builder.Services.Configure<SysAdminControlOptions>(builder.Configuration.GetSection(SysAdminControlOptions.SectionName));
builder.Services.Configure<SysAdminAuthOptions>(builder.Configuration.GetSection(SysAdminAuthOptions.SectionName));
builder.Services.AddSingleton<SysAdminControlState>();

var app = builder.Build();

await using (var startupScope = app.Services.CreateAsyncScope())
{
    var runtimeRepository = startupScope.ServiceProvider.GetRequiredService<IPlatformRuntimeStateRepository>();
    var controlState = startupScope.ServiceProvider.GetRequiredService<SysAdminControlState>();
    var authRepository = startupScope.ServiceProvider.GetRequiredService<ISysAdminAuthRepository>();
    var authOptions = startupScope.ServiceProvider.GetRequiredService<IOptions<SysAdminAuthOptions>>().Value;

    await runtimeRepository.EnsureSchemaAsync(CancellationToken.None);
    await authRepository.EnsureSchemaAsync(CancellationToken.None);

    if (authOptions.Bootstrap.IsActive(builder.Environment.IsDevelopment()))
    {
        await authRepository.EnsureBootstrapAccountAsync(
            authOptions.Bootstrap.Email,
            authOptions.Bootstrap.Password,
            authOptions.Bootstrap.DisplayName,
            CancellationToken.None);
    }

    var persistedMaintenance = await runtimeRepository.GetMaintenanceStateAsync(CancellationToken.None);
    if (persistedMaintenance is null)
    {
        await runtimeRepository.UpsertMaintenanceStateAsync(
            controlState.GetMaintenanceState().ToPlatformMaintenanceState(),
            CancellationToken.None);
    }
    else
    {
        controlState.SetMaintenanceState(persistedMaintenance.ToSummary());
    }
}

app.MapGet("/", () => Results.Ok(new
{
    service = "Citus.SysAdmin.Api",
    status = "platform-core-wired",
    purpose = "system administration and platform core control",
    core = "Citus.Platform.Core"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "Citus.SysAdmin.Api",
    utc = DateTimeOffset.UtcNow
}));

var auth = app.MapGroup("/auth");
var core = app.MapGroup("/core")
    .AddEndpointFilter(RequireSysAdminSessionAsync);
var control = app.MapGroup("/control")
    .AddEndpointFilter(RequireSysAdminSessionAsync);

auth.MapPost(
    "/login",
    async (
        SysAdminLoginRequest request,
        ISysAdminAuthRepository authRepository,
        IOptions<SysAdminAuthOptions> authOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var sessionLifetime = TimeSpan.FromHours(Math.Max(authOptions.Value.SessionHours, 1));
        var result = await authRepository.AuthenticateAsync(
            request.Email,
            request.Password,
            sessionLifetime,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken);

        if (!result.Succeeded)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new SysAdminSessionResponse
        {
            SessionToken = result.SessionToken,
            Session = ToSessionSummaryFromAuthentication(result)
        });
    });

auth.MapGet(
    "/setup",
    async (
        ISysAdminAuthRepository authRepository,
        IOptions<SysAdminAuthOptions> authOptions,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken) =>
    {
        var status = await authRepository.GetSetupStatusAsync(cancellationToken);
        return Results.Ok(ToSetupStatusResponse(status, authOptions.Value, environment));
    });

auth.MapPost(
    "/setup/first-account",
    async (
        SysAdminFirstAccountSetupRequest request,
        ISysAdminAuthRepository authRepository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await authRepository.ProvisionFirstAccountAsync(
                request.Email,
                request.Password,
                request.DisplayName,
                cancellationToken);

            if (!result.Succeeded)
            {
                return Results.BadRequest(new
                {
                    message = result.FailureMessage,
                    code = result.FailureCode
                });
            }

            return Results.Created($"/auth/setup/first-account/{result.SysAdminAccountId}", result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

auth.MapGet(
    "/session",
    async (
        HttpContext httpContext,
        ISysAdminAuthRepository authRepository,
        CancellationToken cancellationToken) =>
    {
        var sessionToken = ReadSysAdminSessionToken(httpContext);
        var result = await authRepository.ValidateSessionAsync(sessionToken, cancellationToken);

        return result.Succeeded
            ? Results.Ok(ToSessionSummaryFromValidation(result))
            : Results.Unauthorized();
    });

auth.MapPost(
    "/logout",
    async (
        HttpContext httpContext,
        ISysAdminAuthRepository authRepository,
        CancellationToken cancellationToken) =>
    {
        var sessionToken = ReadSysAdminSessionToken(httpContext);
        await authRepository.RevokeSessionAsync(sessionToken, cancellationToken);
        return Results.NoContent();
    });

auth.MapPost(
    "/rotate-secret",
    async (
        HttpContext httpContext,
        SysAdminRotateSecretRequest request,
        ISysAdminAuthRepository authRepository,
        CancellationToken cancellationToken) =>
    {
        var result = await authRepository.RotateSecretAsync(
            GetAuthenticatedSession(httpContext).SysAdminAccountId,
            request.CurrentPassword,
            request.NewPassword,
            cancellationToken);

        return result.Succeeded
            ? Results.Ok(result)
            : Results.BadRequest(new
            {
                message = result.FailureMessage,
                code = result.FailureCode
            });
    })
    .AddEndpointFilter(RequireSysAdminSessionAsync);

core.MapGet(
    "/",
    async (IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);
        var modules = await service.ListModulesAsync(cancellationToken);
        var entities = await service.ListEntitiesAsync(cancellationToken);

        return Results.Ok(new
        {
            name = "Citus.Platform.Core",
            inspiration = "WebVella-style metadata-driven ERP kernel adapted for Citus",
            modulesRegistered = modules.Count,
            entitiesRegistered = entities.Count,
            capabilities = new[]
            {
                "bootstrap",
                "module-registry",
                "entity-metadata"
            }
        });
    });

core.MapPost(
    "/bootstrap",
    async (IPlatformBootstrapper bootstrapper, CancellationToken cancellationToken) =>
    {
        var report = await bootstrapper.BootstrapAsync(cancellationToken);
        return Results.Ok(report);
    });

core.MapGet(
    "/modules",
    async (IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);
        var modules = await service.ListModulesAsync(cancellationToken);

        return Results.Ok(modules.Select(module => new
        {
            module.Id,
            module.Key,
            module.Name,
            module.Description,
            module.RoutePrefix,
            module.IsSystemModule,
            module.Capabilities,
            module.EntityNames
        }));
    });

core.MapGet(
    "/entities",
    async (IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);
        var entities = await service.ListEntitiesAsync(cancellationToken);

        return Results.Ok(entities.Select(entity => new
        {
            entity.Id,
            entity.ModuleKey,
            entity.Name,
            entity.Label,
            entity.LabelPlural,
            entity.StorageTable,
            entity.CompanyScoped,
            entity.SystemScoped,
            FieldCount = entity.Fields.Count
        }));
    });

core.MapGet(
    "/entities/{name}",
    async (string name, IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);
        var entity = await service.GetEntityAsync(name, cancellationToken);

        return entity is null
            ? Results.NotFound(new
            {
                message = $"Entity '{name}' is not registered in the platform core."
            })
            : Results.Ok(entity);
    });

core.MapPost(
    "/entities",
    async (UpsertCoreEntityHttpRequest request, IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);

        try
        {
            var entityDefinition = request.ToEntityDefinition();
            await service.UpsertEntityAsync(entityDefinition, cancellationToken);
            var stored = await service.GetEntityAsync(entityDefinition.Name, cancellationToken);
            return Results.Ok(stored);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

app.MapGet("/modules/accounting", () => Results.Ok(new
{
    key = PlatformModuleKeys.Accounting,
    status = "registered-through-platform-core",
    route = "/accounting"
}));

control.MapGet(
    "/context",
    async (HttpContext httpContext, SysAdminControlState state, IPlatformRuntimeStateRepository runtimeRepository, CancellationToken cancellationToken) =>
    {
        var persistedMaintenance = await GetMaintenanceStateAsync(runtimeRepository, state, cancellationToken);
        var operatorSummary = ToOperatorSummary(GetAuthenticatedSession(httpContext));
        return Results.Ok(state.GetContext(operatorSummary) with
        {
            MaintenanceState = persistedMaintenance
        });
    });

control.MapGet(
    "/companies",
    (SysAdminControlState state) => Results.Ok(state.GetCompanies()));

control.MapGet(
    "/users",
    async (
        SysAdminControlState state,
        IPlatformGovernanceRepository governanceRepository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var users = await governanceRepository.ListManagedUsersAsync(cancellationToken);
            return Results.Ok(users.Select(user => new ManagedUserSummary
            {
                Id = user.AccountId,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Username = user.Username,
                IsActive = string.Equals(user.Status, "active", StringComparison.OrdinalIgnoreCase),
                IsSysAdmin = false,
                MfaMode = user.MfaMode,
                ActiveMfaRecoveryStatus = user.ActiveMfaRecoveryStatus,
                LastMfaResetAtUtc = user.LastMfaResetAtUtc,
                LastMfaResetReason = user.LastMfaResetReason,
                Roles = Array.Empty<string>(),
                CompanyCodes = user.CompanyCodes
            }));
        }
        catch
        {
            return Results.Ok(state.GetUsers());
        }
    });

control.MapGet(
    "/mfa-recovery-requests",
    async (
        IPlatformGovernanceRepository governanceRepository,
        CancellationToken cancellationToken) =>
    {
        var requests = await governanceRepository.ListOpenMfaRecoveryRequestsAsync(cancellationToken);
        return Results.Ok(requests.Select(request => new Citus.Ui.Shared.Control.MfaRecoveryRequestSummary
        {
            RequestId = request.RequestId,
            AccountId = request.AccountId,
            DisplayName = request.DisplayName,
            Email = request.Email,
            Username = request.Username,
            CurrentMfaMode = request.CurrentMfaMode,
            Status = request.Status,
            RequestReason = request.RequestReason,
            RequestedAtUtc = request.RequestedAtUtc,
            ReviewReason = request.ReviewReason,
            ReviewedAtUtc = request.ReviewedAtUtc,
            ReviewedByDisplayName = request.ReviewedByDisplayName,
            ExecutionReason = request.ExecutionReason,
            ExecutedAtUtc = request.ExecutedAtUtc,
            ExecutedByDisplayName = request.ExecutedByDisplayName
        }));
    });

control.MapGet(
    "/accounts/{accountId:guid}/mfa-recovery-history",
    async (
        Guid accountId,
        IPlatformGovernanceRepository governanceRepository,
        int? limit,
        CancellationToken cancellationToken) =>
    {
        var requests = await governanceRepository.ListAccountMfaRecoveryHistoryAsync(accountId, limit ?? 10, cancellationToken);
        return Results.Ok(requests.Select(request => new Citus.Ui.Shared.Control.MfaRecoveryRequestSummary
        {
            RequestId = request.RequestId,
            AccountId = request.AccountId,
            DisplayName = request.DisplayName,
            Email = request.Email,
            Username = request.Username,
            CurrentMfaMode = request.CurrentMfaMode,
            Status = request.Status,
            RequestReason = request.RequestReason,
            RequestedAtUtc = request.RequestedAtUtc,
            ReviewReason = request.ReviewReason,
            ReviewedAtUtc = request.ReviewedAtUtc,
            ReviewedByDisplayName = request.ReviewedByDisplayName,
            ExecutionReason = request.ExecutionReason,
            ExecutedAtUtc = request.ExecutedAtUtc,
            ExecutedByDisplayName = request.ExecutedByDisplayName
        }));
    });

control.MapGet(
    "/accounts/{accountId:guid}/mfa-timeline",
    async (
        Guid accountId,
        IPlatformGovernanceRepository governanceRepository,
        int? limit,
        CancellationToken cancellationToken) =>
    {
        var events = await governanceRepository.ListAccountMfaTimelineAsync(accountId, limit ?? 20, cancellationToken);
        return Results.Ok(events.Select(ToPlatformMfaTimelineEntrySummary));
    });

control.MapGet(
    "/companies/{companyId:guid}/memberships",
    async (
        Guid companyId,
        ICompanyMembershipPermissionStore membershipStore,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var memberships = await membershipStore.ListAsync(companyId, cancellationToken);

            return Results.Ok(memberships.Select(membership => new ManagedCompanyMembershipSummary
            {
                MembershipId = membership.MembershipId,
                CompanyId = membership.CompanyId,
                AccountId = membership.UserId,
                Email = membership.Email,
                Username = membership.Username,
                DisplayName = membership.DisplayName,
                Role = membership.Role,
                PermissionTokens = membership.PermissionTokens,
                IsActive = membership.IsActive,
                UpdatedAt = membership.UpdatedAt
            }));
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Unable to load company memberships.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

control.MapGet(
    "/maintenance",
    async (SysAdminControlState state, IPlatformRuntimeStateRepository runtimeRepository, CancellationToken cancellationToken) =>
        Results.Ok(await GetMaintenanceStateAsync(runtimeRepository, state, cancellationToken)));

control.MapGet(
    "/notification-readiness",
    async (IPlatformNotificationReadinessWorkflow workflow, CancellationToken cancellationToken) =>
    {
        var state = await workflow.GetAsync(cancellationToken);
        return Results.Ok(ToNotificationReadinessSummary(state));
    });

control.MapGet(
    "/audit-events",
    async (
        int? limit,
        IPlatformGovernanceRepository governanceRepository,
        CancellationToken cancellationToken) =>
    {
        var events = await governanceRepository.ListRecentAuditEventsAsync(limit ?? 100, cancellationToken);
        return Results.Ok(events.Select(ToPlatformAuditEventSummary));
    });

control.MapPut(
    "/active-company/{companyId:guid}",
    async (Guid companyId, HttpContext httpContext, SysAdminControlState state, IPlatformRuntimeStateRepository runtimeRepository, CancellationToken cancellationToken) =>
    {
        if (!state.TrySetActiveCompany(companyId, ToOperatorSummary(GetAuthenticatedSession(httpContext)), out var context))
        {
            return Results.NotFound(new
            {
                message = $"Company '{companyId}' is not managed by the SysAdmin control context."
            });
        }

        var maintenanceState = await GetMaintenanceStateAsync(runtimeRepository, state, cancellationToken);

        return Results.Ok(context! with
        {
            MaintenanceState = maintenanceState
        });
    });

control.MapPut(
    "/maintenance",
    async (MaintenanceUpdateRequest request, SysAdminControlState state, IPlatformRuntimeStateRepository runtimeRepository, CancellationToken cancellationToken) =>
    {
        var updatedState = state.UpdateMaintenance(request);
        var persistedState = await runtimeRepository.UpsertMaintenanceStateAsync(
            updatedState.ToPlatformMaintenanceState(),
            cancellationToken);

        state.SetMaintenanceState(persistedState.ToSummary());
        return Results.Ok(persistedState.ToSummary());
    });

control.MapPut(
    "/notification-readiness",
    async (
        NotificationReadinessUpdateRequest request,
        IPlatformRuntimeStateRepository runtimeRepository,
        IPlatformNotificationReadinessWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        await runtimeRepository.UpsertNotificationReadinessStateAsync(
            new PlatformNotificationReadinessState
            {
                ConfigPresent = request.ConfigPresent,
                TestStatus = request.TestStatus,
                LastTestedAtUtc = request.LastTestedAtUtc,
                VerificationReady = request.VerificationReady
            },
            cancellationToken);

        var report = await workflow.GetAsync(cancellationToken);
        return Results.Ok(ToNotificationReadinessSummary(report));
    });

control.MapPost(
    "/notification-readiness/test-send",
    async (
        NotificationTestSendRequest request,
        HttpContext httpContext,
        IPlatformNotificationReadinessWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var session = GetAuthenticatedSession(httpContext);
            var result = await workflow.SendTestAsync(
                request.Destination,
                string.IsNullOrWhiteSpace(request.RecipientDisplayName)
                    ? session.DisplayName
                    : request.RecipientDisplayName,
                cancellationToken);

            return Results.Ok(ToNotificationTestSendResult(result));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

control.MapPut(
    "/companies/{companyId:guid}/status",
    async (
        Guid companyId,
        HttpContext httpContext,
        CompanyStatusUpdateRequest request,
        IPlatformGovernanceRepository governanceRepository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await governanceRepository.SetCompanyStatusAsync(
                companyId,
                request.Status,
                request.Reason,
                GetAuthenticatedSession(httpContext).SysAdminAccountId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new
                {
                    message = $"Company '{companyId}' was not found."
                })
                : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

control.MapPut(
    "/accounts/{accountId:guid}/status",
    async (
        Guid accountId,
        HttpContext httpContext,
        AccountStatusUpdateRequest request,
        IPlatformGovernanceRepository governanceRepository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await governanceRepository.SetAccountStatusAsync(
                accountId,
                request.Status,
                request.LockedUntilUtc,
                request.Reason,
                GetAuthenticatedSession(httpContext).SysAdminAccountId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new
                {
                    message = $"Account '{accountId}' was not found."
                })
                : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

control.MapPost(
    "/accounts/{accountId:guid}/password-reset-requests",
    async (
        Guid accountId,
        HttpContext httpContext,
        PasswordResetRequestCommand request,
        IPlatformGovernanceRepository governanceRepository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await governanceRepository.RequestPasswordResetAsync(
                accountId,
                request.Reason,
                GetAuthenticatedSession(httpContext).SysAdminAccountId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new
                {
                    message = $"Account '{accountId}' was not found."
                })
                : Results.Accepted($"/control/accounts/{accountId}/password-reset-requests/{result.RequestId}", result);
        }
        catch (PlatformNotificationDeliveryException ex)
        {
            return Results.Problem(
                title: "Password reset delivery failed.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

control.MapPost(
    "/accounts/{accountId:guid}/mfa-reset",
    async (
        Guid accountId,
        HttpContext httpContext,
        AccountMfaResetRequest request,
        IPlatformGovernanceRepository governanceRepository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await governanceRepository.ResetAccountMfaAsync(
                accountId,
                request.Reason,
                GetAuthenticatedSession(httpContext).SysAdminAccountId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new
                {
                    message = $"Account '{accountId}' was not found."
                })
                : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

control.MapPut(
    "/mfa-recovery-requests/{requestId:guid}/decision",
    async (
        Guid requestId,
        HttpContext httpContext,
        MfaRecoveryReviewRequest request,
        IPlatformGovernanceRepository governanceRepository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await governanceRepository.ReviewMfaRecoveryRequestAsync(
                requestId,
                request.Decision,
                request.Reason,
                GetAuthenticatedSession(httpContext).SysAdminAccountId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new
                {
                    message = $"MFA recovery request '{requestId}' was not found."
                })
                : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

control.MapPost(
    "/mfa-recovery-requests/{requestId:guid}/execute",
    async (
        Guid requestId,
        HttpContext httpContext,
        MfaRecoveryExecuteRequest request,
        IPlatformGovernanceRepository governanceRepository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await governanceRepository.ExecuteMfaRecoveryRequestAsync(
                requestId,
                request.Reason,
                GetAuthenticatedSession(httpContext).SysAdminAccountId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new
                {
                    message = $"MFA recovery request '{requestId}' was not found."
                })
                : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

control.MapPut(
    "/companies/{companyId:guid}/memberships/{membershipId:guid}/role",
    async (
        Guid companyId,
        Guid membershipId,
        HttpContext httpContext,
        CompanyMembershipRoleUpdateRequest request,
        ICompanyMembershipGovernanceWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.ChangeRoleFromSysAdminAsync(
                companyId,
                membershipId,
                request.Role,
                request.Reason,
                GetAuthenticatedSession(httpContext).SysAdminAccountId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

app.Run();

static async Task<MaintenanceStateSummary> GetMaintenanceStateAsync(
    IPlatformRuntimeStateRepository runtimeRepository,
    SysAdminControlState controlState,
    CancellationToken cancellationToken)
{
    var persistedState = await runtimeRepository.GetMaintenanceStateAsync(cancellationToken);

    if (persistedState is null)
    {
        return controlState.GetMaintenanceState();
    }

    var summary = persistedState.ToSummary();
    controlState.SetMaintenanceState(summary);
    return summary;
}

static async ValueTask<object?> RequireSysAdminSessionAsync(
    EndpointFilterInvocationContext context,
    EndpointFilterDelegate next)
{
    var httpContext = context.HttpContext;
    var authRepository = httpContext.RequestServices.GetRequiredService<ISysAdminAuthRepository>();
    var validation = await authRepository.ValidateSessionAsync(
        ReadSysAdminSessionToken(httpContext),
        httpContext.RequestAborted);

    if (!validation.Succeeded)
    {
        return Results.Unauthorized();
    }

    httpContext.Items[typeof(SysAdminAuthSessionSummary)] = ToSessionSummaryFromValidation(validation);
    return await next(context);
}

static SysAdminAuthSessionSummary GetAuthenticatedSession(HttpContext httpContext)
{
    if (httpContext.Items.TryGetValue(typeof(SysAdminAuthSessionSummary), out var session) &&
        session is SysAdminAuthSessionSummary summary)
    {
        return summary;
    }

    throw new InvalidOperationException("SysAdmin session is required.");
}

static SysAdminOperatorSummary ToOperatorSummary(SysAdminAuthSessionSummary session) =>
    new()
    {
        DisplayName = session.DisplayName,
        Email = session.Email,
        Roles = session.Roles
    };

static string ReadSysAdminSessionToken(HttpContext httpContext) =>
    httpContext.Request.Headers[SysAdminAuthConstants.SessionHeaderName].ToString().Trim();

static SysAdminAuthSessionSummary ToSessionSummaryFromAuthentication(SysAdminAuthenticationResult result) =>
    new()
    {
        SysAdminAccountId = result.SysAdminAccountId,
        Email = result.Email,
        DisplayName = result.DisplayName,
        Roles = result.Roles,
        ExpiresAtUtc = result.ExpiresAtUtc
    };

static SysAdminAuthSessionSummary ToSessionSummaryFromValidation(SysAdminSessionValidationResult result) =>
    new()
    {
        SysAdminAccountId = result.SysAdminAccountId,
        Email = result.Email,
        DisplayName = result.DisplayName,
        Roles = result.Roles,
        ExpiresAtUtc = result.ExpiresAtUtc
    };

static NotificationReadinessSummary ToNotificationReadinessSummary(PlatformNotificationReadinessReport state) =>
    new()
    {
        ConfigPresent = state.ConfigPresent,
        TestStatus = state.TestStatus,
        LastTestedAtUtc = state.LastTestedAtUtc,
        VerificationReady = state.VerificationReady,
        IsVerificationDeliveryReady = state.IsVerificationDeliveryReady,
        BlockingReason = state.BlockingReason,
        ConfigurationError = state.ConfigurationError
    };

static NotificationTestSendResult ToNotificationTestSendResult(PlatformNotificationTestSendResult result) =>
    new()
    {
        Succeeded = result.Succeeded,
        ProviderKey = result.ProviderKey,
        Destination = result.Destination,
        FailureMessage = result.FailureMessage,
        Readiness = ToNotificationReadinessSummary(result.Readiness)
    };

static SysAdminSetupStatusResponse ToSetupStatusResponse(
    SysAdminSetupStatus status,
    SysAdminAuthOptions options,
    IWebHostEnvironment environment) =>
    new()
    {
        AccountCount = status.AccountCount,
        HasAnyAccount = status.HasAnyAccount,
        SetupRequired = status.SetupRequired,
        BootstrapSeedingEnabled = options.Bootstrap.Enabled,
        BootstrapSeedingActive = options.Bootstrap.IsActive(environment.IsDevelopment()),
        BootstrapEmailHint = string.IsNullOrWhiteSpace(options.Bootstrap.Email)
            ? string.Empty
            : options.Bootstrap.Email.Trim().ToLowerInvariant()
    };

static PlatformAuditEventSummary ToPlatformAuditEventSummary(PlatformAuditEvent auditEvent) =>
    new()
    {
        AuditId = auditEvent.AuditId,
        CompanyId = auditEvent.CompanyId,
        CompanyCode = auditEvent.CompanyCode,
        CompanyName = auditEvent.CompanyName,
        ScopeLabel = auditEvent.ScopeLabel,
        ActorType = auditEvent.ActorType,
        ActorId = auditEvent.ActorId,
        ActorDisplayName = auditEvent.ActorDisplayName,
        ActorEmail = auditEvent.ActorEmail,
        EntityType = auditEvent.EntityType,
        EntityId = auditEvent.EntityId,
        EntityLabel = auditEvent.EntityLabel,
        Action = auditEvent.Action,
        ActionLabel = auditEvent.ActionLabel,
        Detail = auditEvent.Detail,
        Reason = auditEvent.Reason,
        Highlights = auditEvent.Highlights,
        CreatedAtUtc = auditEvent.CreatedAtUtc
    };

static PlatformMfaTimelineEntrySummary ToPlatformMfaTimelineEntrySummary(PlatformAuditEvent auditEvent) =>
    new()
    {
        Action = auditEvent.Action,
        ActionLabel = auditEvent.ActionLabel,
        Detail = auditEvent.Detail,
        Reason = auditEvent.Reason,
        ActorType = auditEvent.ActorType,
        ActorDisplayName = auditEvent.ActorDisplayName,
        CreatedAtUtc = auditEvent.CreatedAtUtc
    };
