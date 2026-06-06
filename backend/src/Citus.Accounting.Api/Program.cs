using Citus.Accounting.Api;
using Citus.Accounting.Api.Endpoints;
using Citus.Accounting.Api.Startup;
using static Citus.Accounting.Api.AccountingEndpointHelpers;
using static Citus.Accounting.Api.CompanyCurrencyResponseMapper;
using static Citus.Accounting.Api.InventoryItemRequestMapper;
using static Citus.Accounting.Api.Authorization.EndpointApprovalAuthorityHelpers;
using static Citus.Accounting.Api.Endpoints.Support.ReviewMappers;
using static Citus.Accounting.Api.Endpoints.Support.BusinessSessionEndpointHelpers;
using static Citus.Accounting.Api.Endpoints.Support.EndpointRequestHelpers;
using Citus.Accounting.Api.Initialization;
using Citus.Accounting.Api.Tasks;
using Citus.Accounting.Application;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.CoaTemplates;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Companies;
using Citus.Accounting.Application.Invoices;
using Citus.Accounting.Application.Queries;
using Citus.Accounting.Application.Statements;
using Citus.Accounting.Application.Reconciliation;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Journal;
using Citus.Ui.Shared.Reports;
using Citus.Ui.Shared.Shell;
using Citus.Accounting.Infrastructure.Companies;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Invoices;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Citus.Accounting.Infrastructure.Statements;
using Citus.Modules.UnitySearch.Application;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Application.Pricing;
using Citus.Modules.Inventory.Domain.Shared;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Citus.Modules.Tasks.Application;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.Tasks.Domain.Shared.Reports;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Accounts;
using Infrastructure.PostgreSQL.Uom;
using Infrastructure.PostgreSQL.BusinessAuth;
using Infrastructure.PostgreSQL.Banking;
using Infrastructure.PostgreSQL.Company;
using Infrastructure.PostgreSQL.CompanyAccess;
using Infrastructure.PostgreSQL.AP.Bills;
using Infrastructure.PostgreSQL.AP.Expenses;
using Infrastructure.PostgreSQL.AP.PurchaseOrders;
using Infrastructure.PostgreSQL.Counterparties;
using Infrastructure.PostgreSQL.Sales;
using Modules.AP.Bills;
using Modules.AP.Expenses;
using Modules.AP.PurchaseOrders;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Inventory;
using Infrastructure.PostgreSQL.Inventory.Posting;
using Infrastructure.PostgreSQL.Numbering;
using Infrastructure.PostgreSQL.Tax;
using Infrastructure.PostgreSQL.UnitySearch;
using Infrastructure.PostgreSQL.UnityAi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Citus.Accounting.Api.Authorization;
using Modules.CompanyAccess.Memberships;
using Modules.CompanyAccess.SessionContext;
using Npgsql;
using Modules.Company.FeatureManagement;
using Modules.Company.MultiBook;
using Modules.Company.MultiCurrency;
using System.Text;
using System.Threading.RateLimiting;
using JournalEntryNumberLookup = Engines.Numbering.JournalEntry.IJournalEntryNumberLookup;
using GlIJournalEntryLifecycleStore = Modules.GL.JournalEntry.IJournalEntryLifecycleStore;
using GlIJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.IJournalEntryLifecycleWorkflow;
using GlJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.JournalEntryLifecycleWorkflow;

var builder = WebApplication.CreateBuilder(args);

// P0 (Program.cs refactor safety net): force DI lifetime + buildability
// validation in EVERY environment. The host enables these by default only in
// Development, so a captive dependency (a Singleton capturing a Scoped) or an
// unconstructable registration would otherwise stay invisible until it broke
// in Production. Forcing them on makes the staged Program.cs refactor fail
// fast at startup / in CI instead of at runtime.
builder.Host.UseDefaultServiceProvider(static (_, options) =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

builder.UseAccountingSentry();

var connectionString = builder.ResolveAccountingConnectionString();

builder.Services.AddAccountingDataAccessFoundation(connectionString, builder.Configuration);
builder.Services.AddInventorySubledger();
builder.Services.AddBusinessSessionServices();
builder.Services.AddAccountingDocumentRepositories();
builder.Services.AddGeneralLedgerAndFx();
builder.Services.AddCompanyAccessAndPermissions();
builder.Services.AddPricingTasksAndSalesTax(builder.Configuration);
builder.Services.AddPostingEngine();
builder.Services.AddPostingCommandHandlers();
builder.Services.AddSearchCoreMasterDataAndPdf();
builder.Services.AddDeliveryAndDocumentStores();
builder.Services.AddPlatformSchemaAndProvisioning();
builder.Services.AddBusinessAuthAndNotifications();
builder.Services.AddSearchLearningDashboardAndAi();
builder.Services.AddActionCenterTaskProviders();

builder.Services.AddAccountingRateLimiting();

var app = builder.Build();

await AccountingSchemaBootstrapper.ApplyIfEnabledAsync(app);

// ---------------------------------------------------------------------------
// Business sign-in endpoints. Mounted at app root (NOT under /accounting/)
// because the Blazor BusinessAuthenticationClient sends to "auth/login",
// "auth/session", "auth/logout" relative to the API base URL. The shapes
// match the SignInResponse / BusinessAuthSessionSummary contracts the
// client already speaks; AuthenticateAsync / ValidateSessionAsync /
// RevokeSessionAsync delegate to the Postgres-backed
// IPlatformBusinessSessionRepository (same repo SysAdmin uses to verify
// First-Company-Wizard owners).
// ---------------------------------------------------------------------------

// HTTP middleware pipeline: forwarded headers, security headers, generic
// exception handling, HSTS, Sentry tracing, rate limiting, and the
// /internal/* network guard. See AccountingMiddlewareExtensions.
app.UseAccountingPipeline();

app.MapPost(
    "/auth/login",
    async (
        BusinessSignInRequest request,
        HttpContext httpContext,
        Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository repository,
        Modules.CompanyAccess.SessionContext.ICompanySessionContextWorkflow contextWorkflow,
        IConfiguration configuration,
        CancellationToken cancellationToken) =>
    {
        var sessionLifetime = ResolveBusinessSessionLifetime(configuration);
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();

        var result = await repository.AuthenticateAsync(
            request.Email,
            request.Password,
            sessionLifetime,
            remoteIp,
            userAgent,
            cancellationToken);

        if (!result.Succeeded)
        {
            return Results.Json(
                new { message = string.IsNullOrWhiteSpace(result.FailureMessage)
                    ? "Sign-in failed." : result.FailureMessage,
                    code = result.FailureCode },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (result.RequiresSecondFactor)
        {
            // First-pass only — MFA challenge issued. The Blazor client
            // doesn't yet drive a second-factor screen, so we currently
            // surface this as a clear error rather than partial success.
            return Results.Json(
                new { message = "Multi-factor authentication is required for this account; the business shell does not yet support the second-factor step." },
                statusCode: StatusCodes.Status409Conflict);
        }

        var summary = await BuildBusinessSessionSummaryAsync(
            contextWorkflow,
            result.UserId,
            result.ActiveCompanyId,
            cancellationToken);

        if (summary is null)
        {
            return Results.Json(
                new { message = "Sign-in succeeded but the user's company access could not be resolved." },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(new BusinessSignInResponse
        {
            Succeeded = true,
            SessionToken = result.SessionToken,
            Session = summary,
            Message = string.Empty,
            IsBootstrap = false
        });
    })
    .RequireRateLimiting("auth-login");

app.MapGet(
    "/auth/session",
    async (
        HttpContext httpContext,
        Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository repository,
        Modules.CompanyAccess.SessionContext.ICompanySessionContextWorkflow contextWorkflow,
        CancellationToken cancellationToken) =>
    {
        var token = ReadBusinessSessionToken(httpContext);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.Unauthorized();
        }

        var result = await repository.ValidateSessionAsync(token, cancellationToken);
        if (!result.Succeeded)
        {
            return Results.Unauthorized();
        }

        var summary = await BuildBusinessSessionSummaryAsync(
            contextWorkflow,
            result.UserId,
            result.ActiveCompanyId,
            cancellationToken);
        return summary is null ? Results.Unauthorized() : Results.Ok(summary);
    });

app.MapPost(
    "/auth/logout",
    async (
        HttpContext httpContext,
        Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository repository,
        CancellationToken cancellationToken) =>
    {
        var token = ReadBusinessSessionToken(httpContext);
        if (!string.IsNullOrWhiteSpace(token))
        {
            await repository.RevokeSessionAsync(token, cancellationToken);
        }

        return Results.NoContent();
    });

// M17 (AUDIT_2026-05-20 P2-4): switch the session's active company.
// Updates business_sessions.active_company_id on the server (so the
// M17 bind check in BusinessRouteGuard sees the new company) and
// returns the refreshed session summary. The Blazor client uses the
// returned summary to update BusinessShellState BEFORE flipping the
// X-Active-Company-Id header on subsequent requests — order matters
// because the bind check rejects any mismatch between header and
// DB-stored active_company_id.
app.MapPost(
    "/auth/switch-active-company",
    async (
        BusinessSwitchActiveCompanyRequest request,
        HttpContext httpContext,
        Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository repository,
        Modules.CompanyAccess.SessionContext.ICompanySessionContextWorkflow contextWorkflow,
        CancellationToken cancellationToken) =>
    {
        var token = ReadBusinessSessionToken(httpContext);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.Unauthorized();
        }

        if (!CompanyId.TryParse(request.ActiveCompanyId, out var requestedCompanyId))
        {
            return Results.Json(
                new { message = "Active company id is missing or malformed." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await repository.SwitchActiveCompanyAsync(token, requestedCompanyId, cancellationToken);
        if (!result.Succeeded)
        {
            // Distinguish "session not bound to this company at all"
            // (403) from "session invalid / expired" (401). The repo
            // surfaces both as FailureCode strings.
            var status = result.FailureCode switch
            {
                "company_not_available" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status401Unauthorized
            };
            return Results.Json(
                new { message = string.IsNullOrWhiteSpace(result.FailureMessage)
                    ? "Active-company switch failed." : result.FailureMessage,
                    code = result.FailureCode },
                statusCode: status);
        }

        var summary = await BuildBusinessSessionSummaryAsync(
            contextWorkflow,
            result.UserId,
            result.ActiveCompanyId,
            cancellationToken);
        if (summary is null)
        {
            return Results.Json(
                new { message = "Active company switched but the refreshed company access could not be resolved." },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(summary);
    });

// ---------------------------------------------------------------------------
// Self-serve password reset (Business shell only). Pair of public endpoints:
// /auth/forgot-password issues a token + sends an email; /auth/reset-password
// redeems the token and sets a new password. Both deliberately return the
// same shape regardless of whether the email matches a real account, so an
// attacker can't enumerate registered users by timing or response shape.
// ---------------------------------------------------------------------------
app.MapPost(
    "/auth/forgot-password",
    async (
        BusinessForgotPasswordRequest request,
        HttpContext httpContext,
        IConfiguration configuration,
        Citus.Platform.Core.Abstractions.IPlatformBusinessPasswordResetService resetService,
        Citus.Platform.Core.Abstractions.IPlatformVerificationNotificationSender notifier,
        CancellationToken cancellationToken) =>
    {
        // Always respond with this generic ack — never leak whether
        // the email is a known account.
        var ack = new
        {
            ok = true,
            message =
                "If an account matches that email, a reset link has been sent. " +
                "Check your inbox (the link expires in 15 minutes). " +
                "If nothing arrives, your administrator may need to verify the SMTP configuration.",
        };

        if (string.IsNullOrWhiteSpace(request?.Email))
        {
            return Results.Ok(ack);
        }

        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var issued = await resetService.IssueTokenAsync(request.Email, remoteIp, cancellationToken);
        if (issued is null)
        {
            return Results.Ok(ack);
        }

        var publicBaseUrl = (configuration["AppHost:PublicBaseUrl"] ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            // Fall back to the request's own origin if the AppHost
            // public URL hasn't been configured. Better to emit a
            // self-relative link than to no-op the email.
            publicBaseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        }
        var resetUrl = $"{publicBaseUrl}/reset-password?token={Uri.EscapeDataString(issued.PlaintextToken)}";

        // SMTP-not-configured failure is treated as a soft failure
        // here — log the dispatch attempt but still return the ack
        // so the operator-facing message stays generic. Per agreement
        // the ack already hints at "ask admin to check SMTP" if no
        // mail arrives.
        await notifier.SendPasswordResetLinkAsync(
            new Citus.Platform.Core.Runtime.PasswordResetLinkNotificationMessage
            {
                DispatchId = Guid.NewGuid(),
                Destination = issued.Email,
                RecipientDisplayName = issued.DisplayName,
                ResetUrl = resetUrl,
                ExpiresAtUtc = issued.ExpiresAtUtc,
            },
            cancellationToken);

        return Results.Ok(ack);
    });

app.MapPost(
    "/auth/reset-password",
    async (
        BusinessResetPasswordRequest request,
        Citus.Platform.Core.Abstractions.IPlatformBusinessPasswordResetService resetService,
        CancellationToken cancellationToken) =>
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return Results.BadRequest(new
            {
                ok = false,
                code = "missing_fields",
                message = "Reset token and new password are required.",
            });
        }

        var outcome = await resetService.RedeemTokenAsync(
            request.Token, request.NewPassword, cancellationToken);

        if (!outcome.Succeeded)
        {
            return Results.BadRequest(new
            {
                ok = false,
                code = outcome.FailureCode,
                message = outcome.FailureMessage,
            });
        }

        return Results.Ok(new
        {
            ok = true,
            message = "Password updated. You can sign in now.",
        });
    });

var accounting = app.MapGroup("/accounting");

accounting.AddEndpointFilterFactory(
    (factoryContext, next) =>
    {
        return async invocationContext =>
        {
            var services = invocationContext.HttpContext.RequestServices;
            var runtimeStateRepository = services.GetRequiredService<IPlatformRuntimeStateRepository>();
            var routeGuard = services.GetRequiredService<BusinessRouteGuard>();
            var sessionAccessor = services.GetRequiredService<BusinessSessionContextAccessor>();
            var maintenanceState = await runtimeStateRepository.GetMaintenanceStateAsync(invocationContext.HttpContext.RequestAborted);
            var guardResult = await routeGuard.EvaluateAsync(
                invocationContext.HttpContext.Request.Method,
                invocationContext.HttpContext.Request.Headers,
                invocationContext.Arguments as IReadOnlyList<object?> ?? invocationContext.Arguments.ToArray(),
                maintenanceState,
                invocationContext.HttpContext.RequestAborted);

            if (!guardResult.Allowed)
            {
                return Results.Json(
                    new
                    {
                        message = guardResult.Message,
                        maintenanceEnabled = maintenanceState?.Enabled ?? false,
                        maintenanceMessage = maintenanceState?.Message,
                        scheduledUntilUtc = maintenanceState?.ScheduledUntilUtc,
                        requiredHeaders = new[]
                        {
                            Citus.Ui.Shared.Business.BusinessAuthHeaderNames.SessionToken,
                            BusinessSessionHeaders.UserId,
                            BusinessSessionHeaders.ActiveCompanyId
                        }
                    },
                    statusCode: guardResult.StatusCode);
            }

            if (guardResult.Session is not null)
            {
                sessionAccessor.Set(guardResult.Session, guardResult.Resolution);
            }

            return await next(invocationContext);
        };
    });

app.MapGet("/", () => Results.Ok(new
{
    service = "Citus.Accounting.Api",
    status = "registered-through-platform-core",
    authority = "CITUS_PRODUCT_ENGINEERING_AUTHORITY.md",
    storage = "PostgreSQL",
    module = "accounting",
    core = "Citus.Platform.Core"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "Citus.Accounting.Api",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/architecture", () => Results.Ok(new
{
    layers = new[]
    {
        "PlatformCore",
        "Domain",
        "Application",
        "Infrastructure",
        "Api"
    },
    postingRule = "All formal accounting must go through the Posting Engine.",
    moduleRegistration = "accounting module is governed by Citus.Platform.Core metadata"
}));

// /accounting endpoint modules, extracted by domain (P6). Registered on the
// same group instance, so the session/maintenance guard + per-route filters apply.
accounting.MapCompanyMasterDataEndpoints();
accounting.MapInventoryAndTaskEndpoints();
accounting.MapCompanyBookAndOpenItemEndpoints();
accounting.MapSourceDocumentAndReviewEndpoints();
accounting.MapJournalSearchTaxTermsEndpoints();
accounting.MapQuoteSalesOrderAuditEndpoints();
accounting.MapApBillsPoExpensesEndpoints();
accounting.MapBankReconciliationAndAccountsEndpoints();
accounting.MapSearchDashboardFxManualJournalEndpoints();
accounting.MapInvoiceCreditNoteEndpoints();
accounting.MapBillPurchaseOrderEndpoints();
accounting.MapReceiptGrIrAndVendorCreditEndpoints();
accounting.MapPaymentAndCounterpartyDetailEndpoints();


// ============================================================================
// Internal admin trigger: run UnitySearch hint distillation for one company.
//
// This is the manual-fire endpoint while we wait for a hosted scheduler.
// Before P0-6 (C12) it had no auth on purpose and expected to be reached
// only from inside the trusted network — but anyone able to hit the API
// on that network could distill against ANY companyId, consuming the
// configured AI gateway budget against arbitrary tenants and writing
// per-company state into unitysearch_ranking_hints + ai_job_runs.
//
// Current gate (P0-6): a bootstrap token configured at
// `UnityAi:ManualTriggerBootstrapToken` (env var
// `UnityAi__ManualTriggerBootstrapToken`). The request must present it
// as `Authorization: Bearer <token>`. If the token is not configured the
// endpoint refuses 503 — fail closed. This is a temporary gate; the
// final state is migrating the endpoint to the SysAdmin API where the
// existing SysAdmin session filter covers it (tracked as P1 follow-up).
//
// Usage:
//   curl -X POST \
//     -H "Authorization: Bearer ${UNITYAI_MANUAL_TOKEN}" \
//     'http://localhost:5xxx/internal/ai/distill-unitysearch?companyId=<uuid>'
//
// Response:
//   { "jobRunId": "<uuid>", "candidateBuckets": 3, "gatewayCalls": 3,
//     "hintsWritten": 6, "skippedReasonInsufficientActivity": 0,
//     "failedGatewayCalls": 0, "overallStatus": "succeeded", "note": null }
// ============================================================================
app.MapPost(
    "/internal/ai/distill-unitysearch",
    async (
        CompanyId companyId,
        IUnitysearchHintDistillationService distillation,
        IConfiguration configuration,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("ai.distill.unitysearch");

        // P0-6 (C12): bootstrap-token gate.
        var configuredToken = configuration["UnityAi:ManualTriggerBootstrapToken"];
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return Results.Problem(
                detail: "Manual distillation is disabled because UnityAi:ManualTriggerBootstrapToken is not configured. Set the bootstrap token via env or appsettings before invoking this endpoint.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authValues) ||
            authValues.Count == 0)
        {
            return Results.Unauthorized();
        }
        var providedAuth = authValues.ToString();
        if (!providedAuth.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }
        var providedToken = providedAuth.AsSpan("Bearer ".Length).Trim().ToString();
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(providedToken);
        var configuredBytes = System.Text.Encoding.UTF8.GetBytes(configuredToken);
        if (providedBytes.Length != configuredBytes.Length ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes))
        {
            // P0-6 (C12) audit: a caller presented a non-matching bootstrap
            // token — log the source so repeated probing is visible. Never
            // log the token bytes.
            logger.LogWarning(
                "AUDIT internal-ai-trigger rejected (bad token): endpoint={Endpoint} remoteIp={RemoteIp}",
                "distill-unitysearch",
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return Results.Unauthorized();
        }

        if (companyId.Value is null)
        {
            return Results.BadRequest(new { message = "companyId query parameter is required." });
        }

        // P0-6 (C12) audit: record every authorized cross-tenant trigger so an
        // operator can trace who distilled which company. tokenFp is a short
        // SHA-256 prefix — enough to correlate across rotations without
        // persisting the secret. NOTE: these /internal/ai/* endpoints are
        // guarded only by the bootstrap token and accept an arbitrary
        // companyId, so they MUST be bound to the internal/loopback interface
        // (not publicly routable). Full SysAdmin-host migration is the tracked
        // P1 follow-up.
        var tokenFingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(providedBytes)).ToLowerInvariant()[..12];
        logger.LogInformation(
            "AUDIT internal-ai-trigger authorized: endpoint={Endpoint} company={CompanyId} tokenFp={TokenFingerprint} remoteIp={RemoteIp}",
            "distill-unitysearch",
            companyId.Value,
            tokenFingerprint,
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        try
        {
            var result = await distillation.DistillForCompanyAsync(
                companyId: companyId,
                triggeredByUserId: null,
                triggerType: AiJobRunTriggerType.Manual,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Distillation trigger failed for {CompanyId}", companyId);
            return Results.Problem(detail: "The internal AI trigger failed.", statusCode: 500);
        }
    });

// ============================================================================
// Manual trigger for the per-company search-document embedding back-fill
// (Plan C-Population). Same bootstrap-token gate as the distillation
// trigger above. Reads up to maxBatches × 64 rows with embedding IS NULL,
// embeds each batch via the configured provider (text-embedding-3-small
// by default), writes pgvector data back into search_documents.embedding.
//
// Idempotent: repeated calls only pick up rows still NULL — already-
// embedded rows are skipped by the partial index predicate.
//
// Example:
//   curl -s -X POST -H 'Authorization: Bearer <token>' \
//     'http://localhost:5088/internal/ai/backfill-search-doc-embeddings?companyId=<uuid>&maxBatches=4'
// ============================================================================
app.MapPost(
    "/internal/ai/backfill-search-doc-embeddings",
    async (
        CompanyId companyId,
        int? maxBatches,
        ISearchDocumentEmbeddingBackfillService backfill,
        IConfiguration configuration,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("ai.embed.unitysearch.docs");

        var configuredToken = configuration["UnityAi:ManualTriggerBootstrapToken"];
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return Results.Problem(
                detail: "Manual embedding back-fill is disabled because UnityAi:ManualTriggerBootstrapToken is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authValues)
            || authValues.Count == 0)
        {
            return Results.Unauthorized();
        }
        var providedAuth = authValues.ToString();
        if (!providedAuth.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }
        var providedToken = providedAuth.AsSpan("Bearer ".Length).Trim().ToString();
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(providedToken);
        var configuredBytes = System.Text.Encoding.UTF8.GetBytes(configuredToken);
        if (providedBytes.Length != configuredBytes.Length
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes))
        {
            // P0-6 (C12) audit: non-matching bootstrap token — log the source.
            logger.LogWarning(
                "AUDIT internal-ai-trigger rejected (bad token): endpoint={Endpoint} remoteIp={RemoteIp}",
                "backfill-search-doc-embeddings",
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return Results.Unauthorized();
        }

        if (companyId.Value is null)
        {
            return Results.BadRequest(new { message = "companyId query parameter is required." });
        }

        // P0-6 (C12) audit: trace every authorized cross-tenant trigger (see
        // the distillation endpoint above for the full network-binding note).
        var tokenFingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(providedBytes)).ToLowerInvariant()[..12];
        logger.LogInformation(
            "AUDIT internal-ai-trigger authorized: endpoint={Endpoint} company={CompanyId} tokenFp={TokenFingerprint} remoteIp={RemoteIp}",
            "backfill-search-doc-embeddings",
            companyId.Value,
            tokenFingerprint,
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        try
        {
            var result = await backfill.BackfillForCompanyAsync(
                companyId: companyId,
                maxBatches: maxBatches ?? 4,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Doc-embedding back-fill trigger failed for {CompanyId}", companyId);
            return Results.Problem(detail: "The internal AI trigger failed.", statusCode: 500);
        }
    });

accounting.MapV1WriteFlowEndpoints();
accounting.MapMembershipAndPermissionEndpoints();

app.Run();
