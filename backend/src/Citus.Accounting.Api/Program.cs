using Citus.Accounting.Api;
using Citus.Accounting.Api.Endpoints;
using Citus.Accounting.Api.Authentication;
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

app.MapAuthenticationEndpoints();

var accounting = app.MapGroup("/accounting");

accounting.AddBusinessSessionGuard();

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

        var gate = InternalAiTriggerGate.Authorize(httpContext, configuration, companyId, "distill-unitysearch", logger);
        if (gate is not null)
        {
            return gate;
        }

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

        var gate = InternalAiTriggerGate.Authorize(httpContext, configuration, companyId, "backfill-search-doc-embeddings", logger);
        if (gate is not null)
        {
            return gate;
        }

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
