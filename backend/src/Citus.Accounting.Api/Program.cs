using Citus.Accounting.Api;
using static Citus.Accounting.Api.CompanyCurrencyResponseMapper;
using static Citus.Accounting.Api.InventoryItemRequestMapper;
using Citus.Accounting.Api.Initialization;
using Citus.Accounting.Application;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.CoaTemplates;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Companies;
using Citus.Accounting.Application.Invoices;
using Citus.Accounting.Application.Queries;
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
using Citus.Modules.UnitySearch.Application;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Accounts;
using Infrastructure.PostgreSQL.BusinessAuth;
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
using Infrastructure.PostgreSQL.Numbering;
using Infrastructure.PostgreSQL.Tax;
using Infrastructure.PostgreSQL.UnitySearch;
using Infrastructure.PostgreSQL.UnityAi;
using Microsoft.Extensions.Options;
using Modules.CompanyAccess.SessionContext;
using Npgsql;
using Modules.Company.MultiBook;
using Modules.Company.MultiCurrency;
using System.Text;
using JournalEntryNumberLookup = Engines.Numbering.JournalEntry.IJournalEntryNumberLookup;
using GlIJournalEntryLifecycleStore = Modules.GL.JournalEntry.IJournalEntryLifecycleStore;
using GlIJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.IJournalEntryLifecycleWorkflow;
using GlJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.JournalEntryLifecycleWorkflow;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration["CITUS_ACCOUNTING_DB"] ??
    builder.Configuration.GetConnectionString("AccountingCore");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "A PostgreSQL connection string is required. Configure ConnectionStrings:AccountingCore or CITUS_ACCOUNTING_DB.");
}

builder.Services.AddSingleton(new PostgresConnectionFactory(connectionString));
builder.Services.AddSingleton(new PostgreSqlConnectionFactory(connectionString));
builder.Services.AddSingleton<PostgresExecutionContextAccessor>();
builder.Services.AddSingleton(new PlatformPostgresConnectionFactory(connectionString));
builder.Services.Configure<BusinessSessionOptions>(builder.Configuration.GetSection(BusinessSessionOptions.SectionName));
builder.Services.AddSingleton<IPlatformRuntimeStateRepository, PostgresPlatformRuntimeStateRepository>();
builder.Services.AddSingleton<ICompanySessionContextStore, PostgreSqlCompanySessionContextStore>();
builder.Services.AddSingleton<ICompanySessionContextWorkflow, CompanySessionContextWorkflow>();
builder.Services.AddSingleton<IInventoryFoundationStore, PostgreSqlInventoryFoundationStore>();
builder.Services.AddSingleton<IInventoryReceiptStore, PostgreSqlInventoryReceiptStore>();
builder.Services.AddSingleton<IReceiptInventoryActivationStore, PostgreSqlReceiptInventoryActivationStore>();
builder.Services.AddSingleton<IReceiptInventoryValuationStore, PostgreSqlReceiptInventoryValuationStore>();
builder.Services.AddSingleton<IReceiptInventoryCostLayerEmissionStore, PostgreSqlReceiptInventoryCostLayerEmissionStore>();
builder.Services.AddSingleton<IReceiptGrIrBridgeStore, PostgreSqlReceiptGrIrBridgeStore>();
builder.Services.AddSingleton<IInventoryIssueStore, PostgreSqlInventoryIssueStore>();
builder.Services.AddSingleton<IInventoryShipmentStore, PostgreSqlInventoryShipmentStore>();
builder.Services.AddSingleton(
    static services => new BusinessSessionDirectory(
        services.GetRequiredService<IOptions<BusinessSessionOptions>>(),
        services.GetService<ICompanySessionContextWorkflow>()));
builder.Services.AddScoped<BusinessSessionContextAccessor>();
builder.Services.AddSingleton<BusinessSessionRequestReader>();
builder.Services.AddSingleton<BusinessRequestContractGuard>();
builder.Services.AddSingleton<BusinessRouteGuard>();
builder.Services.AddScoped<IManualJournalDocumentRepository, PostgresManualJournalDocumentRepository>();
builder.Services.AddScoped<IInvoiceDocumentRepository, PostgresInvoiceDocumentRepository>();
builder.Services.AddScoped<ICreditNoteDocumentRepository, PostgresCreditNoteDocumentRepository>();
builder.Services.AddScoped<IBillDocumentRepository, PostgresBillDocumentRepository>();
builder.Services.AddScoped<IBillReceiptMatchingRepository, PostgresBillReceiptMatchingRepository>();
builder.Services.AddScoped<IReceiptDocumentRepository, PostgresReceiptDocumentRepository>();
builder.Services.AddScoped<IPurchaseOrderDocumentRepository, PostgresPurchaseOrderDocumentRepository>();
builder.Services.AddScoped<IVendorCreditDocumentRepository, PostgresVendorCreditDocumentRepository>();
builder.Services.AddScoped<IReceivePaymentDocumentRepository, PostgresReceivePaymentDocumentRepository>();
builder.Services.AddScoped<ICreditApplicationDocumentRepository, PostgresCreditApplicationDocumentRepository>();
builder.Services.AddScoped<IPayBillDocumentRepository, PostgresPayBillDocumentRepository>();
builder.Services.AddScoped<IVendorCreditApplicationDocumentRepository, PostgresVendorCreditApplicationDocumentRepository>();
builder.Services.AddScoped<IFxRevaluationDocumentRepository, PostgresFxRevaluationDocumentRepository>();
builder.Services.AddScoped<IAccountingReportRepository, PostgresAccountingReportRepository>();
builder.Services.AddScoped<IAccountingDocumentReviewRepository, PostgresAccountingDocumentReviewRepository>();
builder.Services.AddScoped<IJournalEntryReviewRepository, PostgresJournalEntryReviewRepository>();
builder.Services.AddScoped<IReceiptGrIrPostingRepository, PostgresReceiptGrIrPostingRepository>();
builder.Services.AddScoped<IReceiptGrIrClearingAccountPolicyRepository, PostgresReceiptGrIrClearingAccountPolicyRepository>();
builder.Services.AddScoped<IReceiptGrIrApSettlementControlStore, PostgresReceiptGrIrApSettlementControlStore>();
builder.Services.AddScoped<IReceiptGrIrSettlementPostingRepository, PostgresReceiptGrIrSettlementPostingRepository>();
builder.Services.AddSingleton<JournalEntryNumberLookup, PostgreSqlJournalEntryNumberLookup>();
builder.Services.AddSingleton<GlIJournalEntryLifecycleStore, PostgreSqlJournalEntryLifecycleStore>();
builder.Services.AddSingleton<GlIJournalEntryLifecycleWorkflow, GlJournalEntryLifecycleWorkflow>();
builder.Services.AddScoped<IFxSnapshotRepository, PostgresFxSnapshotRepository>();
builder.Services.AddScoped<ICompanyBookPolicyStore, PostgreSqlCompanyBookPolicyStore>();
builder.Services.AddScoped<ICompanyBookPolicyWorkflow, CompanyBookPolicyWorkflow>();
builder.Services.AddScoped<ICompanyCurrencyProvisioningStore, PostgreSqlCompanyCurrencyProvisioningStore>();
builder.Services.AddScoped<ICompanyCurrencyGovernanceWorkflow, CompanyCurrencyGovernanceWorkflow>();
builder.Services.AddScoped<IArOpenItemRepository, PostgresArOpenItemRepository>();
builder.Services.AddScoped<IApOpenItemRepository, PostgresApOpenItemRepository>();
builder.Services.AddScoped<IOpenItemAdjustmentAccountMappingRepository, PostgresOpenItemAdjustmentAccountMappingRepository>();
builder.Services.AddScoped<ISettlementApplicationRepository, PostgresSettlementApplicationRepository>();
builder.Services.AddScoped<IFxRevaluationApplyRepository, PostgresFxRevaluationApplyRepository>();
builder.Services.AddScoped<IUnitOfWork, PostgresUnitOfWork>();
builder.Services.AddScoped<IPostingValidator, DefaultPostingValidator>();
builder.Services.AddScoped<ITaxEngine, NullTaxEngine>();
builder.Services.AddScoped<IFxResolutionService, LocalFirstFxResolutionService>();
builder.Services.AddSingleton<IFxRateCacheRepository, PostgresFxRateCacheRepository>();
builder.Services.AddScoped<IRecommendedFxRateService, LocalFirstRecommendedFxRateService>();
builder.Services.AddHttpClient<IFrankfurterFxRateClient, FrankfurterFxRateClient>(client =>
{
    client.BaseAddress = new Uri(FrankfurterFxRateClient.ProviderBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.Add("User-Agent", "Citus.Accounting.Api/1.0");
});
builder.Services.AddScoped<IPostingFragmentBuilder, AccountingPostingFragmentBuilder>();
builder.Services.AddScoped<IJournalAggregator, DefaultJournalAggregator>();
builder.Services.AddScoped<IJournalEntryWriter, PostgresJournalEntryWriter>();
builder.Services.AddScoped<IPostingEngine, DefaultPostingEngine>();
builder.Services.AddScoped<PostManualJournalCommandHandler>();
builder.Services.AddScoped<PostInvoiceCommandHandler>();
builder.Services.AddScoped<PostCreditNoteCommandHandler>();
builder.Services.AddScoped<PostBillCommandHandler>();
builder.Services.AddScoped<PostReceiptWorkflow>();
builder.Services.AddScoped<PostReceiptGrIrCommandHandler>();
builder.Services.AddScoped<ExecuteReceiptGrIrSettlementCommandHandler>();
builder.Services.AddScoped<PostReceiptGrIrSettlementJournalCommandHandler>();
builder.Services.AddScoped<ClearReceiptGrIrSettlementOpenItemCommandHandler>();
builder.Services.AddScoped<ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler>();
builder.Services.AddScoped<PostVendorCreditCommandHandler>();
builder.Services.AddScoped<PrepareReceivePaymentDraftCommandHandler>();
builder.Services.AddScoped<PostReceivePaymentCommandHandler>();
builder.Services.AddScoped<PostCreditApplicationCommandHandler>();
builder.Services.AddScoped<PreparePayBillDraftCommandHandler>();
builder.Services.AddScoped<PostPayBillCommandHandler>();
builder.Services.AddScoped<PostVendorCreditApplicationCommandHandler>();
builder.Services.AddScoped<PostArOpenItemAdjustmentCommandHandler>();
builder.Services.AddScoped<PostApOpenItemAdjustmentCommandHandler>();
builder.Services.AddScoped<PrepareFxRevaluationBatchCommandHandler>();
builder.Services.AddScoped<PrepareFxRevaluationUnwindBatchCommandHandler>();
builder.Services.AddScoped<PrepareFxRevaluationCascadeUnwindBatchCommandHandler>();
builder.Services.AddScoped<PostFxRevaluationBatchCommandHandler>();
builder.Services.AddScoped<PostFxRevaluationCascadeUnwindCommandHandler>();
builder.Services.AddSingleton<UnitySearchPolicyRegistry>();
builder.Services.AddSingleton<IUnitySearchProjectionStore, PostgreSqlUnitySearchProjectionStore>();
builder.Services.AddSingleton<IUnitySearchQueryService, PostgreSqlUnitySearchQueryService>();
builder.Services.AddSingleton<IUnitySearchStatsStore, PostgreSqlUnitySearchStatsStore>();
// Inner engine registered as the concrete type so the unityAI reranking
// decorator below can take it as a dependency without a self-cycle.
builder.Services.AddSingleton<UnitySearchEngine>();

// Per-user profile overrides (display name today; future: avatar / locale).
// Persists across bootstrap-session reloads so name changes stick.
builder.Services.AddSingleton<IUserProfileOverrideStore, PostgreSqlUserProfileOverrideStore>();

// Tax code catalog (per-company). Reads/writes the existing tax_codes
// table from the migration draft; safe defaults fill the columns the V1
// settings UI does not yet expose (recoverability_mode, account refs).
builder.Services.AddSingleton<ITaxCodeStore, PostgreSqlTaxCodeStore>();

// Chart of Accounts (per-company). Reads/writes the existing accounts
// table from the migration draft. The UnitySearch projection store
// (SeedAccountDocumentsAsync) reads the same table on its periodic
// refresh, so newly-created accounts surface in pickers automatically.
// is_system rows are protected: update / activate-toggle refuse to
// modify them so AR/AP/FX control accounts stay stable.
builder.Services.AddSingleton<IAccountStore, PostgreSqlAccountStore>();

// Customer master data (per-company). Anchors invoices, receive
// payments, and AR open-item tracking. Entity numbers are
// auto-generated to match the platform-wide ENYYYYxxxxxxxx contract.
builder.Services.AddSingleton<ICustomerStore, PostgreSqlCustomerStore>();

// Read-only company profile lookup (legal_name, address, contacts) for
// surfaces that print the company on a document — invoice / quote / PO
// PDFs, email signatures, etc. Read path only; writes go through the
// SysAdmin First-Company Wizard.
builder.Services.AddSingleton<ICompanyProfileQuery, PostgresCompanyProfileQuery>();

// Invoice PDF rendering (Batch 1 of the invoice-send / template work).
// QuestPDF runs CPU-only, no external dependency. Community license is
// free for any company with annual revenue under USD $1M.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
builder.Services.AddSingleton<IInvoicePdfRenderer, QuestPdfInvoiceRenderer>();

// AES-GCM protector for SysAdmin-entered secrets — SMTP password and
// AI provider API key live in Postgres in encrypted form, decrypted
// just-in-time by the SMTP / AI senders. Same key as TOTP, distinct
// envelope prefix.
builder.Services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformSecretProtector,
    Citus.Platform.Infrastructure.Persistence.PlatformSecretProtector>();

// Invoice email send (Batch 2). The SMTP sender reuses the platform's
// PlatformEmailDeliveryOptions so SysAdmin verification mail and
// Business invoice mail share one outbound configuration. Send-history
// rows go to a separate append-only ledger so the posting-engine
// schema stays untouched.
builder.Services.AddSingleton<IInvoiceEmailSender, SmtpInvoiceEmailSender>();
builder.Services.AddSingleton<IInvoiceSendHistoryStore, PostgresInvoiceSendHistoryStore>();

// Invoice templates (Batch 3). Each company gets three starter templates
// (Modern / Classic / Minimal) seeded lazily on first access, with the
// "Modern" preset auto-marked default. Operators customize via
// Settings -> Invoice templates; the chosen default's branding flows
// through every PDF download and email send.
builder.Services.AddSingleton<IInvoiceTemplateStore, PostgresInvoiceTemplateStore>();

// Vendor master data (per-company). AP-side mirror of ICustomerStore;
// anchors bills, pay-bill settlement, and AP aging.
builder.Services.AddSingleton<IVendorStore, PostgreSqlVendorStore>();

// Payment terms catalog (per-company). Backs Settings → Payment Terms
// and the per-vendor Payment Term picker. net_days drives bill due
// dates downstream; the catalog is intentionally minimal for V1.
builder.Services.AddSingleton<IPaymentTermStore, PostgreSqlPaymentTermStore>();

// Sales-side pre-billing documents: Quotes (a.k.a. estimates) and the
// Sales Orders they convert into. Neither hits the GL — they live as
// informational documents until invoiced through the existing Invoice
// flow.
builder.Services.AddSingleton<IQuoteStore, PostgreSqlQuoteStore>();
builder.Services.AddSingleton<ISalesOrderStore, PostgreSqlSalesOrderStore>();

// AP-side: Bill (vendor invoice) draft + lifecycle. The heavy posting
// pipeline (PostBillCommandHandler, FX snapshot, AP open item) stays
// in Citus.Accounting.Infrastructure; this store is the document-level
// CRUD surface for the Bill page. V1 drives status transitions only;
// the GL writes wire in alongside the PO + Inventory batch.
builder.Services.AddSingleton<IBillStore, PostgreSqlBillStore>();

// AP-side: Purchase Order document surface. Owns ap_purchase_orders /
// ap_purchase_order_lines — distinct from the inventory-grade
// purchase_orders table that the existing posting infrastructure owns.
// Convergence between the two PO surfaces is a migration item for the
// Inventory batch.
builder.Services.AddSingleton<IPurchaseOrderStore, PostgreSqlPurchaseOrderStore>();

// AP-side: Expense (cash outflow) document surface. Owns expenses /
// expense_lines. Posted-only state machine — Expense reflects payments
// already made, no Draft. V1 framework writes the document but defers
// the journal-entry pipeline alongside the Bill GL integration batch.
builder.Services.AddSingleton<IExpenseStore, PostgreSqlExpenseStore>();

// CoA starter templates. Static C# data (no DB tables); the seeder is
// additive — re-applying the same template skips rows that already
// exist by (company_id, code).
builder.Services.AddSingleton<ICoaTemplateRegistry, StaticCoaTemplateRegistry>();
builder.Services.AddSingleton<ICoaTemplateSeeder, CoaTemplateSeeder>();

// ----- unityAI V1 -------------------------------------------------------
// Authority: AI_PRODUCT_ARCHITECTURE.md
// Defaults are conservative: gateway off, AI hints pending, traces sampled.
builder.Services.AddSingleton<UnityAiFeatureFlagAccessor>();
builder.Services.AddSingleton<PostgreSqlUnityAiSchemaInitializer>();

// Platform schema setup. Runs unconditionally in every environment because
// the Accounting API's accounts / tax codes / journal entries / FX writes
// FK into currency_catalog, companies, users, company_memberships, and
// these tables (plus the ISO 4217 currency rows) must exist before any
// business write. PostgresPlatformFirstCompanyProvisioningRepository
// guarantees idempotency via IF NOT EXISTS / ON CONFLICT DO NOTHING.
builder.Services.AddSingleton<Citus.Platform.Core.Runtime.SysAdminPasswordHasher>();
builder.Services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformFirstCompanyProvisioningRepository,
    Citus.Platform.Infrastructure.Persistence.PostgresPlatformFirstCompanyProvisioningRepository>();
builder.Services.AddSingleton<PlatformSchemaInitializer>();

// Business sign-in / session endpoints. The repo is the same one SysAdmin
// has been using for first-company-wizard creds — it owns the users /
// company_memberships / business_sessions schema, hashes passwords with
// SysAdminPasswordHasher, and supports MFA. Wiring it here lets the Business
// shell complete a real sign-in for owners provisioned through the wizard.
// SmtpPlatformVerificationNotificationSender is registered for the MFA
// branch; users with mfa_mode='none' (the default for wizard-created
// owners) never trigger the sender, so missing SMTP config is harmless
// until MFA is explicitly enabled.
// SMTP config lives in the platform_smtp_config singleton row, read
// through IPlatformEmailDeliveryConfigResolver. Both senders
// (verification mail in SysAdmin + invoice mail in Accounting) hit the
// same row, so configuring SMTP once in SysAdmin → Operations → SMTP
// covers both paths.
builder.Services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformSmtpConfigStore,
    Citus.Platform.Infrastructure.Persistence.PostgresPlatformSmtpConfigStore>();
builder.Services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformEmailDeliveryConfigResolver,
    Citus.Platform.Infrastructure.Notifications.PlatformEmailDeliveryConfigResolver>();
builder.Services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformVerificationNotificationSender,
    Citus.Platform.Infrastructure.Notifications.SmtpPlatformVerificationNotificationSender>();
// Brute-force lockout: 5 fails in 15 min → 15-min temporary lock,
// 3 temp locks in 36 h → permanent lock. Must be registered BEFORE
// the business session repo so the latter's constructor injection
// resolves it.
builder.Services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformLoginLockoutPolicy,
    Citus.Platform.Infrastructure.Persistence.PostgresPlatformLoginLockoutPolicy>();
builder.Services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository,
    Citus.Platform.Infrastructure.Persistence.PostgresPlatformBusinessSessionRepository>();
// Self-serve forgot-password flow for the Business shell. SysAdmin
// shell uses a different (manual-grant) reset path; not wired here.
builder.Services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformBusinessPasswordResetService,
    Citus.Platform.Infrastructure.Persistence.PostgresPlatformBusinessPasswordResetService>();
builder.Services.AddSingleton<IAiJobRunStore, PostgreSqlAiJobRunStore>();
builder.Services.AddSingleton<IAiRequestLogStore, PostgreSqlAiRequestLogStore>();
builder.Services.AddSingleton<IUnitysearchEventStore, PostgreSqlUnitysearchEventStore>();
builder.Services.AddSingleton<IUnitysearchUsageStatStore, PostgreSqlUnitysearchUsageStatStore>();
builder.Services.AddSingleton<IUnitysearchPairStatStore, PostgreSqlUnitysearchPairStatStore>();
builder.Services.AddSingleton<IUnitysearchRecentQueryStore, PostgreSqlUnitysearchRecentQueryStore>();
builder.Services.AddSingleton<IUnitysearchRankingHintStore, PostgreSqlUnitysearchRankingHintStore>();
builder.Services.AddSingleton<IUnitysearchDecisionTraceStore, PostgreSqlUnitysearchDecisionTraceStore>();
builder.Services.AddSingleton<IUnitysearchRankingEngine, UnitysearchRankingEngine>();
// Register the reranking decorator as the IUnitySearchEngine the rest of
// the API resolves. It wraps the concrete UnitySearchEngine and falls
// through to its ordering when the learning flag is off or when the
// ranking engine throws — search must never break because of unityAI.
builder.Services.AddSingleton<IUnitySearchEngine>(sp => new UnitysearchAiRerankingEngine(
    inner: sp.GetRequiredService<UnitySearchEngine>(),
    ranking: sp.GetRequiredService<IUnitysearchRankingEngine>(),
    flags: sp.GetRequiredService<UnityAiFeatureFlagAccessor>(),
    logger: sp.GetRequiredService<ILogger<UnitysearchAiRerankingEngine>>()));
builder.Services.AddSingleton<IReportUsageEventStore, PostgreSqlReportUsageEventStore>();
builder.Services.AddSingleton<IReportUsageStatStore, PostgreSqlReportUsageStatStore>();
builder.Services.AddSingleton<IDashboardUserWidgetStore, PostgreSqlDashboardUserWidgetStore>();
builder.Services.AddSingleton<IDashboardWidgetSuggestionStore, PostgreSqlDashboardWidgetSuggestionStore>();
builder.Services.AddSingleton<IDashboardSuggestionService, DashboardSuggestionService>();
builder.Services.AddSingleton<IActionCenterTaskStore, PostgreSqlActionCenterTaskStore>();
builder.Services.AddSingleton<IActionCenterTaskEventStore, PostgreSqlActionCenterTaskEventStore>();
builder.Services.AddSingleton<IActionCenterTaskService, ActionCenterTaskService>();
builder.Services.AddSingleton<IUnityAiProvider, NoopAiProvider>();
builder.Services.AddSingleton<IUnityAiModelRouter, NoopUnityAiModelRouter>();
builder.Services.AddSingleton<IUnityAiPromptRegistry, NoopUnityAiPromptRegistry>();
builder.Services.AddSingleton<IUnityAiStructuredOutputValidator, NoopUnityAiStructuredOutputValidator>();
builder.Services.AddSingleton<IUnityAiGateway, UnityAiGateway>();
builder.Services.AddSingleton<IAccountingCopilotPlanner, NoopAccountingCopilotPlanner>();

// V1 ships only the system-setup task provider as a real rule, plus null
// providers for AR / AP / banking / sales-tax — those domains do not yet
// expose the read shape these rules need, and the architecture forbids
// fabricating tasks. Each null provider logs once-per-call so the gap
// is operationally visible.
builder.Services.AddSingleton<IActionCenterTaskProvider>(sp => new SystemSetupActionCenterTaskProvider(
    readSnapshotAsync: (companyId, ct) =>
    {
        // V1: optimistic snapshot — assume profile complete and SMTP configured
        // until a real settings reader is wired in. Wiring this to the real
        // company_settings table is the first follow-up.
        return ValueTask.FromResult(new SystemSetupSnapshot(SmtpConfigured: true, CompanyProfileComplete: true));
    },
    sp.GetRequiredService<ILogger<SystemSetupActionCenterTaskProvider>>()));
builder.Services.AddSingleton<IActionCenterTaskProvider>(sp => new NullActionCenterTaskProvider(
    name: "ar_overdue_invoices",
    missingDomain: "AR open-invoice aggregate not exposed",
    sp.GetRequiredService<ILogger<NullActionCenterTaskProvider>>()));
builder.Services.AddSingleton<IActionCenterTaskProvider>(sp => new NullActionCenterTaskProvider(
    name: "ap_bills_due_soon",
    missingDomain: "AP unpaid-bill aggregate not exposed",
    sp.GetRequiredService<ILogger<NullActionCenterTaskProvider>>()));
builder.Services.AddSingleton<IActionCenterTaskProvider>(sp => new NullActionCenterTaskProvider(
    name: "bank_unmatched_transactions",
    missingDomain: "banking / reconciliation module not yet present",
    sp.GetRequiredService<ILogger<NullActionCenterTaskProvider>>()));
builder.Services.AddSingleton<IActionCenterTaskProvider>(sp => new NullActionCenterTaskProvider(
    name: "sales_tax_filing_due",
    missingDomain: "sales-tax filing calendar not yet exposed",
    sp.GetRequiredService<ILogger<NullActionCenterTaskProvider>>()));

var app = builder.Build();

await using (var startupScope = app.Services.CreateAsyncScope())
{
    var runtimeStateRepository = startupScope.ServiceProvider.GetRequiredService<IPlatformRuntimeStateRepository>();
    var adjustmentAccountMappingRepository = startupScope.ServiceProvider.GetRequiredService<IOpenItemAdjustmentAccountMappingRepository>();
    var unitySearchProjectionStore = startupScope.ServiceProvider.GetRequiredService<IUnitySearchProjectionStore>();
    var unityAiSchemaInitializer = startupScope.ServiceProvider.GetRequiredService<PostgreSqlUnityAiSchemaInitializer>();
    var userProfileOverrideStore = startupScope.ServiceProvider.GetRequiredService<IUserProfileOverrideStore>();
    var taxCodeStore = startupScope.ServiceProvider.GetRequiredService<ITaxCodeStore>();
    var accountStore = startupScope.ServiceProvider.GetRequiredService<IAccountStore>();
    var customerStore = startupScope.ServiceProvider.GetRequiredService<ICustomerStore>();
    var vendorStore = startupScope.ServiceProvider.GetRequiredService<IVendorStore>();
    var paymentTermStore = startupScope.ServiceProvider.GetRequiredService<IPaymentTermStore>();
    var quoteStore = startupScope.ServiceProvider.GetRequiredService<IQuoteStore>();
    var salesOrderStore = startupScope.ServiceProvider.GetRequiredService<ISalesOrderStore>();
    var billStore = startupScope.ServiceProvider.GetRequiredService<IBillStore>();
    var purchaseOrderStore = startupScope.ServiceProvider.GetRequiredService<IPurchaseOrderStore>();
    var expenseStore = startupScope.ServiceProvider.GetRequiredService<IExpenseStore>();
    var fxRateCache = startupScope.ServiceProvider.GetRequiredService<IFxRateCacheRepository>();
    var invoiceSendHistoryStore = startupScope.ServiceProvider.GetRequiredService<IInvoiceSendHistoryStore>();
    var invoiceTemplateStore = startupScope.ServiceProvider.GetRequiredService<IInvoiceTemplateStore>();
    var smtpConfigStore = startupScope.ServiceProvider.GetRequiredService<Citus.Platform.Core.Abstractions.IPlatformSmtpConfigStore>();
    var platformSchema = startupScope.ServiceProvider.GetRequiredService<PlatformSchemaInitializer>();
    var businessSessionRepository = startupScope.ServiceProvider.GetRequiredService<Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository>();
    var businessPasswordResetService = startupScope.ServiceProvider.GetRequiredService<Citus.Platform.Core.Abstractions.IPlatformBusinessPasswordResetService>();
    var loginLockoutPolicy = startupScope.ServiceProvider.GetRequiredService<Citus.Platform.Core.Abstractions.IPlatformLoginLockoutPolicy>();
    await runtimeStateRepository.EnsureSchemaAsync(CancellationToken.None);
    await adjustmentAccountMappingRepository.EnsureSchemaAsync(CancellationToken.None);
    await unitySearchProjectionStore.EnsureSchemaAsync(CancellationToken.None);
    await unityAiSchemaInitializer.EnsureSchemaAsync(CancellationToken.None);
    await userProfileOverrideStore.EnsureSchemaAsync(CancellationToken.None);
    // Platform tables (currency_catalog, companies, users, company_memberships,
    // company_books, etc.) must exist before any accountStore / taxCodeStore
    // insert that FKs into them. The Accounting API used to assume the
    // SysAdmin First-Company Wizard had run first to create those tables and
    // seed the catalog; this initializer runs the same idempotent schema
    // setup directly so the API works end-to-end without external bootstrapping.
    await platformSchema.EnsureAsync(CancellationToken.None);
    await taxCodeStore.EnsureSchemaAsync(CancellationToken.None);
    await accountStore.EnsureSchemaAsync(CancellationToken.None);
    await customerStore.EnsureSchemaAsync(CancellationToken.None);
    await vendorStore.EnsureSchemaAsync(CancellationToken.None);
    await paymentTermStore.EnsureSchemaAsync(CancellationToken.None);
    await quoteStore.EnsureSchemaAsync(CancellationToken.None);
    await salesOrderStore.EnsureSchemaAsync(CancellationToken.None);
    await billStore.EnsureSchemaAsync(CancellationToken.None);
    await purchaseOrderStore.EnsureSchemaAsync(CancellationToken.None);
    await expenseStore.EnsureSchemaAsync(CancellationToken.None);
    await fxRateCache.EnsureSchemaAsync(CancellationToken.None);
    await invoiceSendHistoryStore.EnsureSchemaAsync(CancellationToken.None);
    await invoiceTemplateStore.EnsureSchemaAsync(CancellationToken.None);
    await smtpConfigStore.EnsureSchemaAsync(CancellationToken.None);
    // business_sessions / mfa_challenges / mfa_enrollments tables need
    // to exist before /auth/login can issue tokens or fetch user records.
    await businessSessionRepository.EnsureSchemaAsync(CancellationToken.None);
    await businessPasswordResetService.EnsureSchemaAsync(CancellationToken.None);
    await loginLockoutPolicy.EnsureSchemaAsync(CancellationToken.None);
}

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
    });

app.MapGet(
    "/auth/session",
    async (
        HttpContext httpContext,
        Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository repository,
        Modules.CompanyAccess.SessionContext.ICompanySessionContextWorkflow contextWorkflow,
        CancellationToken cancellationToken) =>
    {
        if (!httpContext.Request.Headers.TryGetValue(
                Citus.Ui.Shared.Business.BusinessAuthHeaderNames.SessionToken,
                out var tokenValues))
        {
            return Results.Unauthorized();
        }

        var token = tokenValues.FirstOrDefault();
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
        if (httpContext.Request.Headers.TryGetValue(
                Citus.Ui.Shared.Business.BusinessAuthHeaderNames.SessionToken,
                out var tokenValues))
        {
            var token = tokenValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
            {
                await repository.RevokeSessionAsync(token, cancellationToken);
            }
        }

        return Results.NoContent();
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

static TimeSpan ResolveBusinessSessionLifetime(IConfiguration configuration)
{
    var hours = configuration.GetValue<int?>("BusinessAuthentication:SessionHours") ?? 12;
    if (hours <= 0) hours = 12;
    return TimeSpan.FromHours(hours);
}

static async Task<Citus.Ui.Shared.Business.BusinessAuthSessionSummary?> BuildBusinessSessionSummaryAsync(
    Modules.CompanyAccess.SessionContext.ICompanySessionContextWorkflow contextWorkflow,
    Guid userId,
    Guid activeCompanyId,
    CancellationToken cancellationToken)
{
    var context = await contextWorkflow.GetAsync(userId, activeCompanyId, cancellationToken);
    if (context is null)
    {
        return null;
    }

    var active = context.AvailableCompanies.FirstOrDefault(c => c.Id == activeCompanyId)
        ?? context.AvailableCompanies.FirstOrDefault();
    if (active is null)
    {
        return null;
    }

    return new Citus.Ui.Shared.Business.BusinessAuthSessionSummary
    {
        User = new Citus.Ui.Shared.Business.BusinessUserSummary
        {
            Id = context.User.Id,
            DisplayName = context.User.DisplayName,
            Email = context.User.Email,
            Username = context.User.Username,
            Roles = context.User.Roles.ToArray()
        },
        ActiveCompany = ToBusinessCompanySummary(active),
        AvailableCompanies = context.AvailableCompanies.Select(ToBusinessCompanySummary).ToArray()
    };
}

static Citus.Ui.Shared.Business.BusinessCompanySummary ToBusinessCompanySummary(
    SharedKernel.CompanyAccess.CompanyAccessCompanySummary company) =>
    new()
    {
        Id = company.Id,
        CompanyCode = company.CompanyCode,
        CompanyName = company.CompanyName,
        BaseCurrencyCode = company.BaseCurrencyCode,
        MultiCurrencyEnabled = company.MultiCurrencyEnabled,
        Status = string.IsNullOrWhiteSpace(company.Status) ? "active" : company.Status,
        IsReadOnly = company.IsReadOnly
    };

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

// -----------------------------------------------------------------------
// Company currencies (multi-currency governance).
//
// Backed by ICompanyCurrencyGovernanceWorkflow which delegates to
// PostgreSqlCompanyCurrencyProvisioningStore. Adding a non-base currency
// flips the company's multi_currency_enabled flag and seeds AR/AP control
// accounts at the next free 11xxx / 20xxx code (per the canonical chart's
// reserve families). The base currency cannot be added, removed, or
// disabled through this surface.
// -----------------------------------------------------------------------
accounting.MapGet(
    "/company/currencies",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        ICompanyCurrencyGovernanceWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var profile = await workflow.GetProfileAsync(session.ActiveCompanyId, cancellationToken);
        return Results.Ok(MapCurrencyProfile(profile));
    });

accounting.MapPost(
    "/company/currencies",
    async (
        EnableCompanyCurrencyHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ICompanyCurrencyGovernanceWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.CurrencyCode))
        {
            return Results.BadRequest(new { message = "currencyCode is required." });
        }

        var actorId = session.UserId;

        try
        {
            var result = await workflow.EnableCurrencyAsync(
                session.ActiveCompanyId,
                request.CurrencyCode,
                actorId,
                cancellationToken);
            return Results.Ok(new
            {
                Profile = MapCurrencyProfile(result.Profile),
                ProvisionedControlAccounts = result.ProvisionedControlAccounts.Select(static account => new
                {
                    account.AccountId,
                    account.Code,
                    account.Name,
                    account.CurrencyCode,
                    account.SystemRole,
                    account.WasCreated
                })
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

// -----------------------------------------------------------------------
// Recommended FX rate.
//
// Returns the suggested per-document FX rate for the given posting date,
// looking up D-1 in the global fx_rates_daily cache and falling through
// to a live frankfurter call (and from there to a most-recent-business-
// close cache lookup if frankfurter is unreachable).
//
// The recommendation is what the UI pre-fills into a document's fx_rate
// field. The user can override; the override is what posts. This
// endpoint is read-only from the caller's perspective even though it
// may write to the cache as a side effect.
// -----------------------------------------------------------------------
accounting.MapGet(
    "/fx-rates/recommended",
    async (
        DateOnly date,
        string baseCode,
        string quoteCode,
        IRecommendedFxRateService rateService,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(baseCode) || string.IsNullOrWhiteSpace(quoteCode))
        {
            return Results.BadRequest(new { message = "baseCode and quoteCode are required." });
        }

        var rate = await rateService.GetAsync(date, baseCode, quoteCode, cancellationToken);
        if (rate is null)
        {
            return Results.NotFound(new
            {
                message = $"No recommended FX rate available for {baseCode}->{quoteCode} on {date:yyyy-MM-dd}."
            });
        }

        return Results.Ok(new
        {
            rate.RateDate,
            rate.BaseCurrencyCode,
            rate.QuoteCurrencyCode,
            rate.Rate,
            rate.Source,
            rate.IsStale
        });
    });

// -----------------------------------------------------------------------
// Customer master data.
//
// V1 surface: list + create. Update / deactivate land in a follow-up
// once the form supports an edit mode. Reads / writes the per-company
// customers table; entity_number is auto-generated server-side to
// match the platform-wide ENYYYYxxxxxxxx contract. Active company id
// resolves from the BusinessSession header — callers don't pass it.
// -----------------------------------------------------------------------
accounting.MapGet(
    "/customers",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        ICustomerStore store,
        bool? includeInactive,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var rows = await store.ListAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
        return Results.Ok(rows);
    });

accounting.MapGet(
    "/customers/{customerId:guid}",
    async (
        Guid customerId,
        BusinessSessionContextAccessor sessionAccessor,
        ICustomerStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var customer = await store.GetByIdAsync(session.ActiveCompanyId, customerId, cancellationToken);
        return customer is null ? Results.NotFound() : Results.Ok(customer);
    });

accounting.MapPost(
    "/customers",
    async (
        CustomerUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ICustomerStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.BadRequest(new { message = "Display name is required." });
        }
        if (string.IsNullOrWhiteSpace(request.DefaultCurrencyCode))
        {
            return Results.BadRequest(new { message = "Default currency code is required." });
        }

        try
        {
            var saved = await store.CreateAsync(
                session.ActiveCompanyId,
                new CustomerUpsertRequest(
                    DisplayName: request.DisplayName,
                    DefaultCurrencyCode: request.DefaultCurrencyCode,
                    Email: request.Email,
                    Phone: request.Phone,
                    AddressLine: request.AddressLine,
                    City: request.City,
                    ProvinceState: request.ProvinceState,
                    PostalCode: request.PostalCode,
                    Country: request.Country,
                    TaxId: request.TaxId,
                    Notes: request.Notes,
                    PaymentTermId: request.PaymentTermId),
                cancellationToken);
            return Results.Ok(saved);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Unique constraint hit (entity_number collision is the realistic
            // case — random 8-digit seeds collide rarely but it's possible).
            // Surface as a friendly retry hint rather than a 500.
            return Results.Conflict(new { message = "Could not allocate a unique entity number. Please try saving again." });
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            return Results.BadRequest(new { message = $"Currency '{request.DefaultCurrencyCode}' is not available in this company. Enable it in Settings → Multi-currency." });
        }
    });

accounting.MapPut(
    "/customers/{customerId:guid}",
    async (
        Guid customerId,
        CustomerUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ICustomerStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.BadRequest(new { message = "Display name is required." });
        }
        if (string.IsNullOrWhiteSpace(request.DefaultCurrencyCode))
        {
            return Results.BadRequest(new { message = "Default currency code is required." });
        }

        try
        {
            var saved = await store.UpdateAsync(
                session.ActiveCompanyId,
                customerId,
                new CustomerUpsertRequest(
                    DisplayName: request.DisplayName,
                    DefaultCurrencyCode: request.DefaultCurrencyCode,
                    Email: request.Email,
                    Phone: request.Phone,
                    AddressLine: request.AddressLine,
                    City: request.City,
                    ProvinceState: request.ProvinceState,
                    PostalCode: request.PostalCode,
                    Country: request.Country,
                    TaxId: request.TaxId,
                    Notes: request.Notes,
                    PaymentTermId: request.PaymentTermId),
                cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            return Results.BadRequest(new { message = $"Currency '{request.DefaultCurrencyCode}' is not available in this company. Enable it in Settings → Multi-currency." });
        }
    });

// Customer shipping-address history — backs the AddressEditor drawer's
// "Use a previous shipping address" picker. Distinct shipping_*
// values from the customer's historical quotes + sales_orders, ranked
// most-recent-first then by usage count.
accounting.MapGet(
    "/customers/{customerId:guid}/shipping-addresses",
    async (
        Guid customerId,
        BusinessSessionContextAccessor sessionAccessor,
        ICustomerStore store,
        int? limit,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var rows = await store.ListShippingAddressHistoryAsync(
            session.ActiveCompanyId,
            customerId,
            limit ?? 20,
            cancellationToken);

        return Results.Ok(rows.Select(r => new
        {
            r.AddressLine,
            r.City,
            r.ProvinceState,
            r.PostalCode,
            r.Country,
            r.UsageCount,
            r.LastUsedOn,
        }));
    });

// -----------------------------------------------------------------------
// Vendor master data — AP-side mirror of /accounting/customers.
// -----------------------------------------------------------------------
accounting.MapGet(
    "/vendors",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IVendorStore store,
        bool? includeInactive,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var rows = await store.ListAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
        return Results.Ok(rows);
    });

accounting.MapGet(
    "/vendors/{vendorId:guid}",
    async (
        Guid vendorId,
        BusinessSessionContextAccessor sessionAccessor,
        IVendorStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var vendor = await store.GetByIdAsync(session.ActiveCompanyId, vendorId, cancellationToken);
        return vendor is null ? Results.NotFound() : Results.Ok(vendor);
    });

accounting.MapPost(
    "/vendors",
    async (
        VendorUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IVendorStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.BadRequest(new { message = "Display name is required." });
        }
        if (string.IsNullOrWhiteSpace(request.DefaultCurrencyCode))
        {
            return Results.BadRequest(new { message = "Default currency code is required." });
        }

        try
        {
            var saved = await store.CreateAsync(
                session.ActiveCompanyId,
                new VendorUpsertRequest(
                    DisplayName: request.DisplayName,
                    DefaultCurrencyCode: request.DefaultCurrencyCode,
                    Email: request.Email,
                    Phone: request.Phone,
                    AddressLine: request.AddressLine,
                    City: request.City,
                    ProvinceState: request.ProvinceState,
                    PostalCode: request.PostalCode,
                    Country: request.Country,
                    TaxId: request.TaxId,
                    Notes: request.Notes,
                    PaymentTermId: request.PaymentTermId),
                cancellationToken);
            return Results.Ok(saved);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { message = "Could not allocate a unique entity number. Please try saving again." });
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            return Results.BadRequest(new { message = $"Currency '{request.DefaultCurrencyCode}' is not available in this company. Enable it in Settings → Multi-currency." });
        }
    });

accounting.MapPut(
    "/vendors/{vendorId:guid}",
    async (
        Guid vendorId,
        VendorUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IVendorStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.BadRequest(new { message = "Display name is required." });
        }
        if (string.IsNullOrWhiteSpace(request.DefaultCurrencyCode))
        {
            return Results.BadRequest(new { message = "Default currency code is required." });
        }

        try
        {
            var saved = await store.UpdateAsync(
                session.ActiveCompanyId,
                vendorId,
                new VendorUpsertRequest(
                    DisplayName: request.DisplayName,
                    DefaultCurrencyCode: request.DefaultCurrencyCode,
                    Email: request.Email,
                    Phone: request.Phone,
                    AddressLine: request.AddressLine,
                    City: request.City,
                    ProvinceState: request.ProvinceState,
                    PostalCode: request.PostalCode,
                    Country: request.Country,
                    TaxId: request.TaxId,
                    Notes: request.Notes,
                    PaymentTermId: request.PaymentTermId),
                cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            return Results.BadRequest(new { message = $"Currency '{request.DefaultCurrencyCode}' is not available in this company. Enable it in Settings → Multi-currency." });
        }
    });

// -----------------------------------------------------------------------
// Inventory items (Products & Services).
//
// V1 surface: list / create / update / activate-toggle. Items come in
// three kinds (Stock, Non-stock, Service). Stock items carry inventory
// settings (costing method, backorder, low-stock activity, default
// inventory asset / COGS / write-off / purchase-variance accounts);
// Non-stock and Service items use only the pricing + accounting
// defaults. The store's existing schema accepts both shapes — the
// per-kind validation lives in the Blazor form so the API can stay
// generic.
//
// Active company id + user id resolve from the BusinessSession header.
// -----------------------------------------------------------------------
accounting.MapGet(
    "/items",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IInventoryFoundationStore store,
        bool? includeInactive,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var rows = await store.ListItemsAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
        return Results.Ok(rows.Select(MapItemSummary));
    });

accounting.MapPost(
    "/items",
    async (
        InventoryItemUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IInventoryFoundationStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        if (session.UserId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateItemRequest(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var itemId = await store.SaveItemAsync(
                BuildItemUpsertRequest(session.ActiveCompanyId, session.UserId, itemId: null, request),
                cancellationToken);

            // Re-fetch the saved row so the response carries the same shape
            // as GET /items (including auto-set fields like created_at).
            var rows = await store.ListItemsAsync(session.ActiveCompanyId, includeInactive: true, cancellationToken);
            var saved = rows.FirstOrDefault(r => r.Id == itemId);
            return saved is null ? Results.NoContent() : Results.Ok(MapItemSummary(saved));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPut(
    "/items/{itemId:guid}",
    async (
        Guid itemId,
        InventoryItemUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IInventoryFoundationStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        if (session.UserId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateItemRequest(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            await store.SaveItemAsync(
                BuildItemUpsertRequest(session.ActiveCompanyId, session.UserId, itemId, request),
                cancellationToken);

            var rows = await store.ListItemsAsync(session.ActiveCompanyId, includeInactive: true, cancellationToken);
            var saved = rows.FirstOrDefault(r => r.Id == itemId);
            return saved is null ? Results.NoContent() : Results.Ok(MapItemSummary(saved));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/items/{itemId:guid}/activate",
    async (
        Guid itemId,
        BusinessSessionContextAccessor sessionAccessor,
        IInventoryFoundationStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        try
        {
            await store.SetItemActiveAsync(session.ActiveCompanyId, itemId, isActive: true, cancellationToken);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/items/{itemId:guid}/deactivate",
    async (
        Guid itemId,
        BusinessSessionContextAccessor sessionAccessor,
        IInventoryFoundationStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        try
        {
            await store.SetItemActiveAsync(session.ActiveCompanyId, itemId, isActive: false, cancellationToken);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { message = ex.Message });
        }
    });

accounting.MapGet(
    "/company-books",
    async ([AsParameters] CompanyBookGovernanceLookupQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var results = await workflow.ListBookGovernanceAsync(
            query.CompanyId,
            asOfDate,
            cancellationToken);

        return Results.Ok(new
        {
            AsOfDate = asOfDate,
            Books = results.Select(result => new
            {
                Book = new
                {
                    result.Book.BookId,
                    result.Book.CompanyId,
                    result.Book.BookCode,
                    result.Book.BookName,
                    result.Book.BookRole,
                    result.Book.AccountingStandard,
                    result.Book.BookBaseCurrencyCode,
                    result.Book.FunctionalCurrencyCode,
                    result.Book.PresentationCurrencyCode,
                    result.Book.IsPrimary,
                    result.Book.IsAdjustmentOnly,
                    result.Book.EffectiveFrom,
                    result.Book.IsActive
                },
                RemeasurementPolicy = result.RemeasurementPolicy is null
                    ? null
                    : new
                    {
                        result.RemeasurementPolicy.PolicyId,
                        result.RemeasurementPolicy.CompanyId,
                        result.RemeasurementPolicy.BookId,
                        result.RemeasurementPolicy.RateType,
                        result.RemeasurementPolicy.QuoteBasis,
                        result.RemeasurementPolicy.RateUseCase,
                        result.RemeasurementPolicy.PostingReason,
                        result.RemeasurementPolicy.RevaluationProfile,
                        result.RemeasurementPolicy.FxRoundingPolicy,
                        result.RemeasurementPolicy.EffectiveFrom,
                        result.RemeasurementPolicy.IsActive
                    },
                MigrationEligibility = new
                {
                    result.MigrationEligibility.ChangeMode,
                    result.MigrationEligibility.EvaluationBasis,
                    result.MigrationEligibility.HasCompanyPostedHistory,
                    result.MigrationEligibility.HasBookSpecificRevaluationHistory,
                    result.MigrationEligibility.DirectEditAllowed,
                    result.MigrationEligibility.Reason
                },
                GovernanceSignals = new
                {
                    result.GovernanceSignals.HasClosedPeriods,
                    result.GovernanceSignals.HasIssuedReports,
                    result.GovernanceSignals.HasFiledTax,
                    Signals = result.GovernanceSignals.Signals.Select(signal => new
                    {
                        signal.SignalId,
                        signal.CompanyId,
                        signal.BookId,
                        signal.SignalType,
                        signal.SignalDate,
                        signal.ReferenceLabel,
                        signal.Notes,
                        signal.CreatedByUserId,
                        signal.CreatedAt
                    })
                }
            })
        });
    });

accounting.MapGet(
    "/company-books/{bookId:guid}/governance-signals",
    async (Guid bookId, [AsParameters] CompanyBookGovernanceSignalsLookupQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var result = await workflow.GetGovernanceSignalsAsync(
            query.CompanyId,
            bookId,
            asOfDate,
            cancellationToken);

        return Results.Ok(new
        {
            BookId = bookId,
            AsOfDate = asOfDate,
            result.HasClosedPeriods,
            result.HasIssuedReports,
            result.HasFiledTax,
            Signals = result.Signals.Select(signal => new
            {
                signal.SignalId,
                signal.CompanyId,
                signal.BookId,
                signal.SignalType,
                signal.SignalDate,
                signal.ReferenceLabel,
                signal.Notes,
                signal.CreatedByUserId,
                signal.CreatedAt
            })
        });
    });

accounting.MapPost(
    "/company-books/{bookId:guid}/governance-signals",
    async (Guid bookId, CreateCompanyBookGovernanceSignalHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.CreateGovernanceSignalAsync(
                request.CompanyId,
                bookId,
                request.SignalType,
                request.SignalDate,
                request.ReferenceLabel,
                request.Notes,
                request.UserId,
                cancellationToken);

            return Results.Ok(new
            {
                Signal = new
                {
                    result.Signal.SignalId,
                    result.Signal.CompanyId,
                    result.Signal.BookId,
                    result.Signal.SignalType,
                    result.Signal.SignalDate,
                    result.Signal.ReferenceLabel,
                    result.Signal.Notes,
                    result.Signal.CreatedByUserId,
                    result.Signal.CreatedAt
                },
                Summary = new
                {
                    result.Summary.HasClosedPeriods,
                    result.Summary.HasIssuedReports,
                    result.Summary.HasFiledTax,
                    Signals = result.Summary.Signals.Select(signal => new
                    {
                        signal.SignalId,
                        signal.CompanyId,
                        signal.BookId,
                        signal.SignalType,
                        signal.SignalDate,
                        signal.ReferenceLabel,
                        signal.Notes,
                        signal.CreatedByUserId,
                        signal.CreatedAt
                    })
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/company-books/{bookId:guid}/close-periods",
    async (Guid bookId, RegisterCompanyBookClosedPeriodHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.RegisterClosedPeriodAsync(
                request.CompanyId,
                bookId,
                request.PeriodEndDate,
                request.ReferenceLabel,
                request.Notes,
                request.UserId,
                cancellationToken);

            return Results.Ok(new
            {
                Signal = new
                {
                    result.Signal.SignalId,
                    result.Signal.CompanyId,
                    result.Signal.BookId,
                    result.Signal.SignalType,
                    result.Signal.SignalDate,
                    result.Signal.ReferenceLabel,
                    result.Signal.Notes,
                    result.Signal.CreatedByUserId,
                    result.Signal.CreatedAt
                },
                Summary = new
                {
                    result.Summary.HasClosedPeriods,
                    result.Summary.HasIssuedReports,
                    result.Summary.HasFiledTax,
                    Signals = result.Summary.Signals.Select(signal => new
                    {
                        signal.SignalId,
                        signal.CompanyId,
                        signal.BookId,
                        signal.SignalType,
                        signal.SignalDate,
                        signal.ReferenceLabel,
                        signal.Notes,
                        signal.CreatedByUserId,
                        signal.CreatedAt
                    })
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/company-books/{bookId:guid}/issued-statements",
    async (Guid bookId, RegisterCompanyBookIssuedStatementHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.RegisterIssuedStatementAsync(
                request.CompanyId,
                bookId,
                request.IssuedOn,
                request.StatementLabel,
                request.Notes,
                request.UserId,
                cancellationToken);

            return Results.Ok(new
            {
                Signal = new
                {
                    result.Signal.SignalId,
                    result.Signal.CompanyId,
                    result.Signal.BookId,
                    result.Signal.SignalType,
                    result.Signal.SignalDate,
                    result.Signal.ReferenceLabel,
                    result.Signal.Notes,
                    result.Signal.CreatedByUserId,
                    result.Signal.CreatedAt
                },
                Summary = new
                {
                    result.Summary.HasClosedPeriods,
                    result.Summary.HasIssuedReports,
                    result.Summary.HasFiledTax,
                    Signals = result.Summary.Signals.Select(signal => new
                    {
                        signal.SignalId,
                        signal.CompanyId,
                        signal.BookId,
                        signal.SignalType,
                        signal.SignalDate,
                        signal.ReferenceLabel,
                        signal.Notes,
                        signal.CreatedByUserId,
                        signal.CreatedAt
                    })
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/company-books/{bookId:guid}/filed-tax",
    async (Guid bookId, RegisterCompanyBookFiledTaxHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.RegisterFiledTaxAsync(
                request.CompanyId,
                bookId,
                request.FiledOn,
                request.FilingLabel,
                request.Notes,
                request.UserId,
                cancellationToken);

            return Results.Ok(new
            {
                Signal = new
                {
                    result.Signal.SignalId,
                    result.Signal.CompanyId,
                    result.Signal.BookId,
                    result.Signal.SignalType,
                    result.Signal.SignalDate,
                    result.Signal.ReferenceLabel,
                    result.Signal.Notes,
                    result.Signal.CreatedByUserId,
                    result.Signal.CreatedAt
                },
                Summary = new
                {
                    result.Summary.HasClosedPeriods,
                    result.Summary.HasIssuedReports,
                    result.Summary.HasFiledTax,
                    Signals = result.Summary.Signals.Select(signal => new
                    {
                        signal.SignalId,
                        signal.CompanyId,
                        signal.BookId,
                        signal.SignalType,
                        signal.SignalDate,
                        signal.ReferenceLabel,
                        signal.Notes,
                        signal.CreatedByUserId,
                        signal.CreatedAt
                    })
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/company-books/governed-change-preview",
    async (CompanyBookGovernedChangePreviewHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var asOfDate = request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var result = await workflow.PreviewGovernedChangeAsync(
                request.CompanyId,
                request.BookId,
                asOfDate,
                new CompanyBookProposedChangeSet(
                    request.IsPrimary,
                    request.AccountingStandard,
                    request.BookBaseCurrencyCode,
                    request.FunctionalCurrencyCode,
                    request.PresentationCurrencyCode,
                    request.RateType,
                    request.QuoteBasis,
                    request.RateUseCase,
                    request.PostingReason,
                    request.RevaluationProfile,
                    request.FxRoundingPolicy),
                cancellationToken);

            return Results.Ok(new
            {
                AsOfDate = asOfDate,
                Book = new
                {
                    result.Book.BookId,
                    result.Book.CompanyId,
                    result.Book.BookCode,
                    result.Book.BookName,
                    result.Book.BookRole,
                    result.Book.AccountingStandard,
                    result.Book.BookBaseCurrencyCode,
                    result.Book.FunctionalCurrencyCode,
                    result.Book.PresentationCurrencyCode,
                    result.Book.IsPrimary,
                    result.Book.IsAdjustmentOnly,
                    result.Book.EffectiveFrom,
                    result.Book.IsActive
                },
                CurrentRemeasurementPolicy = result.CurrentRemeasurementPolicy is null
                    ? null
                    : new
                    {
                        result.CurrentRemeasurementPolicy.PolicyId,
                        result.CurrentRemeasurementPolicy.CompanyId,
                        result.CurrentRemeasurementPolicy.BookId,
                        result.CurrentRemeasurementPolicy.RateType,
                        result.CurrentRemeasurementPolicy.QuoteBasis,
                        result.CurrentRemeasurementPolicy.RateUseCase,
                        result.CurrentRemeasurementPolicy.PostingReason,
                        result.CurrentRemeasurementPolicy.RevaluationProfile,
                        result.CurrentRemeasurementPolicy.FxRoundingPolicy,
                        result.CurrentRemeasurementPolicy.EffectiveFrom,
                        result.CurrentRemeasurementPolicy.IsActive
                    },
                ProposedChanges = result.ProposedChanges,
                ChangeImpact = new
                {
                    result.ChangeImpact.HasAnyChange,
                    result.ChangeImpact.ChangedFields,
                    result.ChangeImpact.ChangeCategories,
                    result.ChangeImpact.DirectUpdateAllowed,
                    result.ChangeImpact.GovernedMigrationRequired,
                    result.ChangeImpact.RecommendedPath,
                    result.ChangeImpact.EvaluationBasis,
                    result.ChangeImpact.Reason
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

accounting.MapPost(
    "/company-books/governed-change-requests/prepare",
    async (PrepareCompanyBookGovernedChangeRequestHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var asOfDate = request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var result = await workflow.PrepareGovernedChangeRequestDraftAsync(
                request.CompanyId,
                request.UserId,
                request.BookId,
                asOfDate,
                request.EffectiveFrom,
                new CompanyBookProposedChangeSet(
                    request.IsPrimary,
                    request.AccountingStandard,
                    request.BookBaseCurrencyCode,
                    request.FunctionalCurrencyCode,
                    request.PresentationCurrencyCode,
                    request.RateType,
                    request.QuoteBasis,
                    request.RateUseCase,
                    request.PostingReason,
                    request.RevaluationProfile,
                    request.FxRoundingPolicy),
                cancellationToken);

            return Results.Ok(new
            {
                result.RequestId,
                result.CompanyId,
                result.BookId,
                result.Status,
                result.RequestedAction,
                result.AsOfDate,
                result.EffectiveFrom,
                result.CreatedByUserId,
                result.CreatedAt,
                result.SubmittedByUserId,
                result.SubmittedAt,
                result.CancelledByUserId,
                result.CancelledAt,
                result.AppliedAt,
                Book = new
                {
                    result.Preview.Book.BookId,
                    result.Preview.Book.CompanyId,
                    result.Preview.Book.BookCode,
                    result.Preview.Book.BookName,
                    result.Preview.Book.BookRole,
                    result.Preview.Book.AccountingStandard,
                    result.Preview.Book.BookBaseCurrencyCode,
                    result.Preview.Book.FunctionalCurrencyCode,
                    result.Preview.Book.PresentationCurrencyCode,
                    result.Preview.Book.IsPrimary,
                    result.Preview.Book.IsAdjustmentOnly,
                    result.Preview.Book.EffectiveFrom,
                    result.Preview.Book.IsActive
                },
                CurrentRemeasurementPolicy = result.Preview.CurrentRemeasurementPolicy is null
                    ? null
                    : new
                    {
                        result.Preview.CurrentRemeasurementPolicy.PolicyId,
                        result.Preview.CurrentRemeasurementPolicy.CompanyId,
                        result.Preview.CurrentRemeasurementPolicy.BookId,
                        result.Preview.CurrentRemeasurementPolicy.RateType,
                        result.Preview.CurrentRemeasurementPolicy.QuoteBasis,
                        result.Preview.CurrentRemeasurementPolicy.RateUseCase,
                        result.Preview.CurrentRemeasurementPolicy.PostingReason,
                        result.Preview.CurrentRemeasurementPolicy.RevaluationProfile,
                        result.Preview.CurrentRemeasurementPolicy.FxRoundingPolicy,
                        result.Preview.CurrentRemeasurementPolicy.EffectiveFrom,
                        result.Preview.CurrentRemeasurementPolicy.IsActive
                    },
                result.Preview.ProposedChanges,
                ChangeImpact = new
                {
                    result.Preview.ChangeImpact.HasAnyChange,
                    result.Preview.ChangeImpact.ChangedFields,
                    result.Preview.ChangeImpact.ChangeCategories,
                    result.Preview.ChangeImpact.DirectUpdateAllowed,
                    result.Preview.ChangeImpact.GovernedMigrationRequired,
                    result.Preview.ChangeImpact.RecommendedPath,
                    result.Preview.ChangeImpact.EvaluationBasis,
                    result.Preview.ChangeImpact.Reason
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

accounting.MapGet(
    "/company-books/governed-change-requests",
    async ([AsParameters] CompanyBookGovernedChangeRequestLookupQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        var results = await workflow.ListGovernedChangeRequestDraftsAsync(query.CompanyId, cancellationToken);

        return Results.Ok(new
        {
            Requests = results.Select(result => new
            {
                result.RequestId,
                result.CompanyId,
                result.BookId,
                result.Status,
                result.RequestedAction,
                result.AsOfDate,
                result.EffectiveFrom,
                result.CreatedByUserId,
                result.CreatedAt,
                result.SubmittedByUserId,
                result.SubmittedAt,
                result.CancelledByUserId,
                result.CancelledAt,
                result.AppliedAt,
                Book = new
                {
                    result.Preview.Book.BookId,
                    result.Preview.Book.CompanyId,
                    result.Preview.Book.BookCode,
                    result.Preview.Book.BookName,
                    result.Preview.Book.BookRole,
                    result.Preview.Book.AccountingStandard,
                    result.Preview.Book.BookBaseCurrencyCode,
                    result.Preview.Book.FunctionalCurrencyCode,
                    result.Preview.Book.PresentationCurrencyCode,
                    result.Preview.Book.IsPrimary,
                    result.Preview.Book.IsAdjustmentOnly,
                    result.Preview.Book.EffectiveFrom,
                    result.Preview.Book.IsActive
                },
                CurrentRemeasurementPolicy = result.Preview.CurrentRemeasurementPolicy is null
                    ? null
                    : new
                    {
                        result.Preview.CurrentRemeasurementPolicy.PolicyId,
                        result.Preview.CurrentRemeasurementPolicy.CompanyId,
                        result.Preview.CurrentRemeasurementPolicy.BookId,
                        result.Preview.CurrentRemeasurementPolicy.RateType,
                        result.Preview.CurrentRemeasurementPolicy.QuoteBasis,
                        result.Preview.CurrentRemeasurementPolicy.RateUseCase,
                        result.Preview.CurrentRemeasurementPolicy.PostingReason,
                        result.Preview.CurrentRemeasurementPolicy.RevaluationProfile,
                        result.Preview.CurrentRemeasurementPolicy.FxRoundingPolicy,
                        result.Preview.CurrentRemeasurementPolicy.EffectiveFrom,
                        result.Preview.CurrentRemeasurementPolicy.IsActive
                    },
                result.Preview.ProposedChanges,
                ChangeImpact = new
                {
                    result.Preview.ChangeImpact.HasAnyChange,
                    result.Preview.ChangeImpact.ChangedFields,
                    result.Preview.ChangeImpact.ChangeCategories,
                    result.Preview.ChangeImpact.DirectUpdateAllowed,
                    result.Preview.ChangeImpact.GovernedMigrationRequired,
                    result.Preview.ChangeImpact.RecommendedPath,
                    result.Preview.ChangeImpact.EvaluationBasis,
                    result.Preview.ChangeImpact.Reason
                }
            })
        });
    });

accounting.MapPost(
    "/company-books/governed-change-requests/{requestId:guid}/submit",
    async (Guid requestId, TransitionCompanyBookGovernedChangeRequestHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.SubmitGovernedChangeRequestDraftAsync(
                request.CompanyId,
                requestId,
                request.UserId,
                cancellationToken);

            return Results.Ok(new
            {
                result.RequestId,
                result.CompanyId,
                result.BookId,
                result.Status,
                result.RequestedAction,
                result.AsOfDate,
                result.EffectiveFrom,
                result.CreatedByUserId,
                result.CreatedAt,
                result.SubmittedByUserId,
                result.SubmittedAt,
                result.CancelledByUserId,
                result.CancelledAt,
                result.AppliedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

accounting.MapPost(
    "/company-books/governed-change-requests/{requestId:guid}/cancel",
    async (Guid requestId, TransitionCompanyBookGovernedChangeRequestHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.CancelGovernedChangeRequestDraftAsync(
                request.CompanyId,
                requestId,
                request.UserId,
                cancellationToken);

            return Results.Ok(new
            {
                result.RequestId,
                result.CompanyId,
                result.BookId,
                result.Status,
                result.RequestedAction,
                result.AsOfDate,
                result.EffectiveFrom,
                result.CreatedByUserId,
                result.CreatedAt,
                result.SubmittedByUserId,
                result.SubmittedAt,
                result.CancelledByUserId,
                result.CancelledAt,
                result.AppliedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

accounting.MapGet(
    "/company-books/governed-change-requests/{requestId:guid}/apply-readiness",
    async (Guid requestId, [AsParameters] CompanyBookGovernedChangeRequestReadinessQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var result = await workflow.ValidateGovernedChangeRequestApplyReadinessAsync(
                query.CompanyId,
                requestId,
                asOfDate,
                cancellationToken);

            return Results.Ok(new
            {
                result.RequestId,
                result.Status,
                result.EffectiveFrom,
                result.EvaluatedAt,
                result.CurrentTruthMatchesDraft,
                result.IsReadyToApply,
                result.RequiresNewBookRollout,
                result.Blockers,
                result.Warnings
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

accounting.MapGet(
    "/company-books/remeasurement-policy",
    async ([AsParameters] CompanyBookPolicyLookupQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var result = await workflow.GetRemeasurementPolicyAsync(
                query.CompanyId,
                query.BookId,
                asOfDate,
                cancellationToken);

            return Results.Ok(new
            {
                AsOfDate = asOfDate,
                result.WasProvisioned,
                Book = new
                {
                    result.Book.BookId,
                    result.Book.CompanyId,
                    result.Book.BookCode,
                    result.Book.BookName,
                    result.Book.BookRole,
                    result.Book.AccountingStandard,
                    result.Book.BookBaseCurrencyCode,
                    result.Book.FunctionalCurrencyCode,
                    result.Book.PresentationCurrencyCode,
                    result.Book.IsPrimary,
                    result.Book.IsAdjustmentOnly,
                    result.Book.EffectiveFrom,
                    result.Book.IsActive
                },
                RemeasurementPolicy = new
                {
                    result.RemeasurementPolicy.PolicyId,
                    result.RemeasurementPolicy.CompanyId,
                    result.RemeasurementPolicy.BookId,
                    result.RemeasurementPolicy.RateType,
                    result.RemeasurementPolicy.QuoteBasis,
                    result.RemeasurementPolicy.RateUseCase,
                    result.RemeasurementPolicy.PostingReason,
                    result.RemeasurementPolicy.RevaluationProfile,
                    result.RemeasurementPolicy.FxRoundingPolicy,
                    result.RemeasurementPolicy.EffectiveFrom,
                    result.RemeasurementPolicy.IsActive
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

accounting.MapGet(
    "/session/context",
    async (
        BusinessSessionContextAccessor accessor,
        IPlatformRuntimeStateRepository runtimeStateRepository,
        BusinessSessionDirectory sessionDirectory,
        CancellationToken cancellationToken) =>
    {
        var session = accessor.Current ??
            throw new InvalidOperationException("Business session context was not resolved for the current request.");
        var maintenanceState = await runtimeStateRepository.GetMaintenanceStateAsync(cancellationToken);
        var resolution = accessor.CurrentResolution;
        if (resolution is null)
        {
            var resolved = await sessionDirectory.ResolveAsync(session, cancellationToken);
            if (!resolved.Success || resolved.Resolution is null)
            {
                return Results.Json(
                    new
                    {
                        message = resolved.Error ?? "Business session context could not be resolved for the current environment."
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            resolution = resolved.Resolution;
        }

        return Results.Ok(new BusinessSessionContextSummary
        {
            User = resolution.User,
            ActiveCompany = resolution.ActiveCompany,
            AvailableCompanies = resolution.AvailableCompanies,
            MaintenanceState = new MaintenanceStateSummary
            {
                Enabled = maintenanceState?.Enabled ?? false,
                Message = maintenanceState?.Message ?? "Platform runtime is accepting interactive changes.",
                ScheduledUntilUtc = maintenanceState?.ScheduledUntilUtc
            }
        });
    });

accounting.MapGet(
    "/reports/trial-balance",
    async ([AsParameters] TrialBalanceLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetTrialBalanceAsync(
            new GetTrialBalanceQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapTrialBalanceReport(report));
    });

accounting.MapGet(
    "/reports/trial-balance/export.csv",
    async ([AsParameters] TrialBalanceLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetTrialBalanceAsync(
            new GetTrialBalanceQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportTrialBalance(MapTrialBalanceReport(report));
        return ToCsvFileResult(file);
    });

accounting.MapGet(
    "/reports/income-statement",
    async ([AsParameters] IncomeStatementLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var dateTo = query.DateTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var dateFrom = query.DateFrom ?? new DateOnly(dateTo.Year, dateTo.Month, 1);

        if (dateFrom > dateTo)
        {
            return Results.BadRequest(new
            {
                message = "Income Statement date range is invalid. dateFrom must be on or before dateTo."
            });
        }

        var report = await repository.GetIncomeStatementAsync(
            new GetIncomeStatementQuery(
                new(query.CompanyId),
                dateFrom,
                dateTo,
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapIncomeStatementReport(report));
    });

accounting.MapGet(
    "/reports/income-statement/export.csv",
    async ([AsParameters] IncomeStatementLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var dateTo = query.DateTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var dateFrom = query.DateFrom ?? new DateOnly(dateTo.Year, dateTo.Month, 1);

        if (dateFrom > dateTo)
        {
            return Results.BadRequest(new
            {
                message = "Income Statement date range is invalid. dateFrom must be on or before dateTo."
            });
        }

        var report = await repository.GetIncomeStatementAsync(
            new GetIncomeStatementQuery(
                new(query.CompanyId),
                dateFrom,
                dateTo,
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportIncomeStatement(MapIncomeStatementReport(report));
        return ToCsvFileResult(file);
    });

accounting.MapGet(
    "/reports/balance-sheet",
    async ([AsParameters] BalanceSheetLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetBalanceSheetAsync(
            new GetBalanceSheetQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapBalanceSheetReport(report));
    });

accounting.MapGet(
    "/reports/balance-sheet/export.csv",
    async ([AsParameters] BalanceSheetLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetBalanceSheetAsync(
            new GetBalanceSheetQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportBalanceSheet(MapBalanceSheetReport(report));
        return ToCsvFileResult(file);
    });

accounting.MapGet(
    "/reports/ar-aging",
    async ([AsParameters] ArAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetArAgingAsync(
            new GetArAgingQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapArAgingReport(report));
    });

accounting.MapGet(
    "/reports/ar-aging/export.csv",
    async ([AsParameters] ArAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetArAgingAsync(
            new GetArAgingQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportArAging(MapArAgingReport(report));
        return ToCsvFileResult(file);
    });

accounting.MapGet(
    "/reports/ap-aging",
    async ([AsParameters] ApAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetApAgingAsync(
            new GetApAgingQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapApAgingReport(report));
    });

accounting.MapGet(
    "/reports/ap-aging/export.csv",
    async ([AsParameters] ApAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetApAgingAsync(
            new GetApAgingQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportApAging(MapApAgingReport(report));
        return ToCsvFileResult(file);
    });

// ---------------------------------------------------------------------------
// Sales Overview — Cash Flow band (10 past + current + 3 forecast months)
// and Income Over Time (accrual-basis revenue chart). Both pull from the
// same accounting tables already feeding the AR aging report; new endpoints
// just layer monthly bucketing on top.
// ---------------------------------------------------------------------------
accounting.MapGet(
    "/sales/cash-flow",
    async ([AsParameters] SalesCashFlowLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetSalesCashFlowAsync(
            new GetSalesCashFlowQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapSalesCashFlowReport(report));
    });

accounting.MapGet(
    "/sales/income-over-time",
    async ([AsParameters] IncomeOverTimeLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var to = query.ToDate ?? today;
        // Default window: trailing 12 months ending on the as-of month.
        var defaultFrom = new DateOnly(to.Year, to.Month, 1).AddMonths(-11);
        var from = query.FromDate ?? defaultFrom;

        var report = await repository.GetIncomeOverTimeAsync(
            new GetIncomeOverTimeQuery(
                new(query.CompanyId),
                from,
                to,
                query.CompareToPreviousYear),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapIncomeOverTimeReport(report));
    });

// ---------------------------------------------------------------------------
// Expense Overview — Cash Outflow band (10 past + current + 3 forecast
// months) and Expense Over Time (accrual-basis cost chart). Mirrors the
// Sales Overview endpoints; sources are bills + expenses + pay_bills +
// ap_open_items instead of invoices + receive_payments + ar_open_items.
// ---------------------------------------------------------------------------
accounting.MapGet(
    "/expense/cash-outflow",
    async ([AsParameters] ExpenseCashOutflowLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetExpenseCashOutflowAsync(
            new GetExpenseCashOutflowQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapExpenseCashOutflowReport(report));
    });

accounting.MapGet(
    "/expense/over-time",
    async ([AsParameters] ExpenseOverTimeLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var to = query.ToDate ?? today;
        var defaultFrom = new DateOnly(to.Year, to.Month, 1).AddMonths(-11);
        var from = query.FromDate ?? defaultFrom;

        var report = await repository.GetExpenseOverTimeAsync(
            new GetExpenseOverTimeQuery(
                new(query.CompanyId),
                from,
                to,
                query.CompareToPreviousYear),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapExpenseOverTimeReport(report));
    });

accounting.MapGet(
    "/open-items/ar/{openItemId:guid}",
    async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IArOpenItemRepository openItemRepository, ISettlementApplicationRepository settlementRepository, CancellationToken cancellationToken) =>
    {
        var item = await openItemRepository.GetDrillDownAsync(
            new(query.CompanyId),
            openItemId,
            cancellationToken);

        if (item is null)
        {
            return Results.NotFound(new
            {
                message = "AR open item was not found in the active company context."
            });
        }

        var applications = await settlementRepository.ListApplicationsAsync(
            new(query.CompanyId),
            "ar_open_item",
            openItemId,
            cancellationToken);

        return Results.Ok(new
        {
            OpenItem = new
            {
                item.OpenItemId,
                item.OpenItemType,
                CompanyId = item.CompanyId.Value,
                item.PartyRole,
                item.PartyId,
                item.PartyEntityNumber,
                item.PartyDisplayName,
                item.SourceType,
                item.SourceDocumentId,
                item.SourceDocumentDisplayNumber,
                item.DocumentDate,
                item.DueDate,
                item.DocumentCurrencyCode,
                item.BaseCurrencyCode,
                item.BalanceSide,
                item.Status,
                item.OriginalAmountTx,
                item.OriginalAmountBase,
                item.OpenAmountTx,
                item.OpenAmountBase
            },
            Applications = applications.Select(application => new
            {
                application.ApplicationId,
                application.ApplicationType,
                application.SourceType,
                application.SourceDocumentId,
                application.SourceDocumentDisplayNumber,
                application.SourceDocumentDate,
                application.AppliedAmountTx,
                application.AppliedAmountBase,
                application.SettlementFxRate,
                application.RealizedFxAmount,
                application.CreatedAt
            })
        });
    });

accounting.MapGet(
    "/open-item-adjustment-account-mappings",
    async ([AsParameters] OpenItemAdjustmentAccountMappingLookupQuery query, IOpenItemAdjustmentAccountMappingRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.LookupAsync(
                new OpenItemAdjustmentAccountMappingLookupRequest(
                    new(query.CompanyId),
                    query.OpenItemType,
                    query.AdjustmentType,
                    query.IncludeInactive == true,
                    query.BookId,
                    query.PolicyScope,
                    query.SearchText,
                    query.Limit ?? 200),
                cancellationToken);

            return Results.Ok(new
            {
                CompanyId = query.CompanyId,
                OpenItemType = query.OpenItemType,
                AdjustmentType = query.AdjustmentType,
                IncludeInactive = query.IncludeInactive == true,
                query.BookId,
                query.PolicyScope,
                query.SearchText,
                Limit = Math.Clamp(query.Limit ?? 200, 1, 500),
                Summary = result.Summary,
                Mappings = result.Mappings
            });
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/open-item-adjustment-account-mappings",
    async (SaveOpenItemAdjustmentAccountMappingHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IOpenItemAdjustmentAccountMappingRepository repository, CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequireOpenItemAdjustmentAccountMappingManagementAuthority(
            sessionAccessor.Current,
            "save");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
            var result = await repository.SaveAsync(
                new OpenItemAdjustmentAccountMappingSaveRequest(
                    new(request.CompanyId),
                    request.BookId,
                    request.OpenItemType,
                    request.AdjustmentType,
                    request.AdjustmentAccountId,
                    actorId),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/open-item-adjustment-account-mappings/{mappingId:guid}/deactivate",
    async (Guid mappingId, DeactivateOpenItemAdjustmentAccountMappingHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IOpenItemAdjustmentAccountMappingRepository repository, CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequireOpenItemAdjustmentAccountMappingManagementAuthority(
            sessionAccessor.Current,
            "deactivate");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var result = await repository.DeactivateAsync(
            new(request.CompanyId),
            mappingId,
            actorId,
            cancellationToken);

        return result is null
            ? Results.NotFound(new { message = "Open-item adjustment account mapping was not found in the active company context." })
            : Results.Ok(result);
    });

accounting.MapGet(
    "/open-items/ar/{openItemId:guid}/adjustment-preview",
    async (Guid openItemId, [AsParameters] OpenItemAdjustmentPreviewLookupQuery query, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var adjustmentType = string.IsNullOrWhiteSpace(query.AdjustmentType) ? "write_off" : query.AdjustmentType;
        var adjustmentDate = query.AdjustmentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var preview = await openItemRepository.GetAdjustmentPreviewAsync(
            new(query.CompanyId),
            openItemId,
            adjustmentType,
            adjustmentDate,
            query.AdjustmentAmountTx,
            cancellationToken);

        return preview is null
            ? Results.NotFound(new { message = "AR open item was not found in the active company context." })
            : Results.Ok(preview);
    });

accounting.MapPost(
    "/open-items/ar/{openItemId:guid}/adjustment-request",
    async (Guid openItemId, RequestOpenItemAdjustmentHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var adjustmentDate = request.AdjustmentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var attempt = await openItemRepository.RequestAdjustmentAsync(
            new(request.CompanyId),
            openItemId,
            request.AdjustmentType,
            adjustmentDate,
            request.AdjustmentAmountTx,
            actorId,
            request.Reason,
            cancellationToken);

        return attempt is null
            ? Results.NotFound(new { message = "AR open item was not found in the active company context." })
            : Results.Ok(attempt);
    });

accounting.MapGet(
    "/open-items/ar/{openItemId:guid}/adjustment-request",
    async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var request = await openItemRepository.GetLatestAdjustmentRequestAsync(
            new(query.CompanyId),
            openItemId,
            cancellationToken);

        return request is null
            ? Results.NotFound(new { message = "No AR open item adjustment request was found for the active company context." })
            : Results.Ok(request);
    });

accounting.MapPost(
    "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/submit",
    async (Guid openItemId, Guid requestId, TransitionOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var result = await openItemRepository.SubmitAdjustmentRequestAsync(
            new(request.CompanyId),
            openItemId,
            requestId,
            actorId,
            cancellationToken);

        return result is null
            ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
            : Results.Ok(result);
    });

accounting.MapPost(
    "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/cancel",
    async (Guid openItemId, Guid requestId, TransitionOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var result = await openItemRepository.CancelAdjustmentRequestAsync(
            new(request.CompanyId),
            openItemId,
            requestId,
            actorId,
            cancellationToken);

        return result is null
            ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
            : Results.Ok(result);
    });

accounting.MapPost(
    "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/approve",
    async (Guid openItemId, Guid requestId, GovernOpenItemAdjustmentApprovalHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var companyId = new CompanyId(request.CompanyId);
        var currentRequest = await openItemRepository.GetLatestAdjustmentRequestAsync(
            companyId,
            openItemId,
            cancellationToken);

        if (currentRequest is null || currentRequest.RequestId != requestId)
        {
            return Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." });
        }

        var authorityBlock = RequireOpenItemAdjustmentApprovalAuthority(
            sessionAccessor.Current,
            currentRequest,
            "AR",
            "approve");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var result = await openItemRepository.ApproveAdjustmentRequestAsync(
            companyId,
            openItemId,
            requestId,
            actorId,
            cancellationToken);

        return result is null
            ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
            : Results.Ok(result);
    });

accounting.MapPost(
    "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/reject",
    async (Guid openItemId, Guid requestId, GovernOpenItemAdjustmentApprovalHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var companyId = new CompanyId(request.CompanyId);
        var currentRequest = await openItemRepository.GetLatestAdjustmentRequestAsync(
            companyId,
            openItemId,
            cancellationToken);

        if (currentRequest is null || currentRequest.RequestId != requestId)
        {
            return Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." });
        }

        var authorityBlock = RequireOpenItemAdjustmentApprovalAuthority(
            sessionAccessor.Current,
            currentRequest,
            "AR",
            "reject");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var result = await openItemRepository.RejectAdjustmentRequestAsync(
            companyId,
            openItemId,
            requestId,
            actorId,
            cancellationToken);

        return result is null
            ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
            : Results.Ok(result);
    });

accounting.MapGet(
    "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/readiness",
    async (Guid openItemId, Guid requestId, [AsParameters] OpenItemAdjustmentRequestReadinessQuery query, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var readiness = await openItemRepository.GetAdjustmentRequestReadinessAsync(
            new(query.CompanyId),
            openItemId,
            requestId,
            asOfDate,
            cancellationToken);

        return readiness is null
            ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
            : Results.Ok(readiness);
    });

accounting.MapGet(
    "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/execution-plan",
    async (Guid openItemId, Guid requestId, [AsParameters] OpenItemAdjustmentRequestReadinessQuery query, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var plan = await openItemRepository.GetAdjustmentRequestExecutionPlanAsync(
            new(query.CompanyId),
            openItemId,
            requestId,
            asOfDate,
            cancellationToken);

        return plan is null
            ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
            : Results.Ok(plan);
    });

accounting.MapPost(
    "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/execute",
    async (Guid openItemId, Guid requestId, ExecuteOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, PostArOpenItemAdjustmentCommandHandler handler, CancellationToken cancellationToken) =>
    {
        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        if (!actorId.HasValue)
        {
            return Results.BadRequest(new { message = "A user id is required to execute a governed AR open item adjustment." });
        }

        try
        {
            var result = await handler.HandleAsync(
                new PostArOpenItemAdjustmentCommand(
                    new(request.CompanyId),
                    openItemId,
                    requestId,
                    new(actorId.Value),
                    request.AdjustmentAccountId,
                    request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/open-items/ap/{openItemId:guid}",
    async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IApOpenItemRepository openItemRepository, ISettlementApplicationRepository settlementRepository, CancellationToken cancellationToken) =>
    {
        var item = await openItemRepository.GetDrillDownAsync(
            new(query.CompanyId),
            openItemId,
            cancellationToken);

        if (item is null)
        {
            return Results.NotFound(new
            {
                message = "AP open item was not found in the active company context."
            });
        }

        var applications = await settlementRepository.ListApplicationsAsync(
            new(query.CompanyId),
            "ap_open_item",
            openItemId,
            cancellationToken);

        return Results.Ok(new
        {
            OpenItem = new
            {
                item.OpenItemId,
                item.OpenItemType,
                CompanyId = item.CompanyId.Value,
                item.PartyRole,
                item.PartyId,
                item.PartyEntityNumber,
                item.PartyDisplayName,
                item.SourceType,
                item.SourceDocumentId,
                item.SourceDocumentDisplayNumber,
                item.DocumentDate,
                item.DueDate,
                item.DocumentCurrencyCode,
                item.BaseCurrencyCode,
                item.BalanceSide,
                item.Status,
                item.OriginalAmountTx,
                item.OriginalAmountBase,
                item.OpenAmountTx,
                item.OpenAmountBase
            },
            Applications = applications.Select(application => new
            {
                application.ApplicationId,
                application.ApplicationType,
                application.SourceType,
                application.SourceDocumentId,
                application.SourceDocumentDisplayNumber,
                application.SourceDocumentDate,
                application.AppliedAmountTx,
                application.AppliedAmountBase,
                application.SettlementFxRate,
                application.RealizedFxAmount,
                application.CreatedAt
            })
        });
    });

accounting.MapGet(
    "/open-items/ap/{openItemId:guid}/adjustment-preview",
    async (Guid openItemId, [AsParameters] OpenItemAdjustmentPreviewLookupQuery query, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var adjustmentType = string.IsNullOrWhiteSpace(query.AdjustmentType) ? "small_balance_adjustment" : query.AdjustmentType;
        var adjustmentDate = query.AdjustmentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var preview = await openItemRepository.GetAdjustmentPreviewAsync(
            new(query.CompanyId),
            openItemId,
            adjustmentType,
            adjustmentDate,
            query.AdjustmentAmountTx,
            cancellationToken);

        return preview is null
            ? Results.NotFound(new { message = "AP open item was not found in the active company context." })
            : Results.Ok(preview);
    });

accounting.MapPost(
    "/open-items/ap/{openItemId:guid}/adjustment-request",
    async (Guid openItemId, RequestOpenItemAdjustmentHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var adjustmentDate = request.AdjustmentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var attempt = await openItemRepository.RequestAdjustmentAsync(
            new(request.CompanyId),
            openItemId,
            request.AdjustmentType,
            adjustmentDate,
            request.AdjustmentAmountTx,
            actorId,
            request.Reason,
            cancellationToken);

        return attempt is null
            ? Results.NotFound(new { message = "AP open item was not found in the active company context." })
            : Results.Ok(attempt);
    });

accounting.MapGet(
    "/open-items/ap/{openItemId:guid}/adjustment-request",
    async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var request = await openItemRepository.GetLatestAdjustmentRequestAsync(
            new(query.CompanyId),
            openItemId,
            cancellationToken);

        return request is null
            ? Results.NotFound(new { message = "No AP open item adjustment request was found for the active company context." })
            : Results.Ok(request);
    });

accounting.MapPost(
    "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/submit",
    async (Guid openItemId, Guid requestId, TransitionOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var result = await openItemRepository.SubmitAdjustmentRequestAsync(
            new(request.CompanyId),
            openItemId,
            requestId,
            actorId,
            cancellationToken);

        return result is null
            ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
            : Results.Ok(result);
    });

accounting.MapPost(
    "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/cancel",
    async (Guid openItemId, Guid requestId, TransitionOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var result = await openItemRepository.CancelAdjustmentRequestAsync(
            new(request.CompanyId),
            openItemId,
            requestId,
            actorId,
            cancellationToken);

        return result is null
            ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
            : Results.Ok(result);
    });

accounting.MapPost(
    "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/approve",
    async (Guid openItemId, Guid requestId, GovernOpenItemAdjustmentApprovalHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var companyId = new CompanyId(request.CompanyId);
        var currentRequest = await openItemRepository.GetLatestAdjustmentRequestAsync(
            companyId,
            openItemId,
            cancellationToken);

        if (currentRequest is null || currentRequest.RequestId != requestId)
        {
            return Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." });
        }

        var authorityBlock = RequireOpenItemAdjustmentApprovalAuthority(
            sessionAccessor.Current,
            currentRequest,
            "AP",
            "approve");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var result = await openItemRepository.ApproveAdjustmentRequestAsync(
            companyId,
            openItemId,
            requestId,
            actorId,
            cancellationToken);

        return result is null
            ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
            : Results.Ok(result);
    });

accounting.MapPost(
    "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/reject",
    async (Guid openItemId, Guid requestId, GovernOpenItemAdjustmentApprovalHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var companyId = new CompanyId(request.CompanyId);
        var currentRequest = await openItemRepository.GetLatestAdjustmentRequestAsync(
            companyId,
            openItemId,
            cancellationToken);

        if (currentRequest is null || currentRequest.RequestId != requestId)
        {
            return Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." });
        }

        var authorityBlock = RequireOpenItemAdjustmentApprovalAuthority(
            sessionAccessor.Current,
            currentRequest,
            "AP",
            "reject");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        var result = await openItemRepository.RejectAdjustmentRequestAsync(
            companyId,
            openItemId,
            requestId,
            actorId,
            cancellationToken);

        return result is null
            ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
            : Results.Ok(result);
    });

accounting.MapGet(
    "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/readiness",
    async (Guid openItemId, Guid requestId, [AsParameters] OpenItemAdjustmentRequestReadinessQuery query, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var readiness = await openItemRepository.GetAdjustmentRequestReadinessAsync(
            new(query.CompanyId),
            openItemId,
            requestId,
            asOfDate,
            cancellationToken);

        return readiness is null
            ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
            : Results.Ok(readiness);
    });

accounting.MapGet(
    "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/execution-plan",
    async (Guid openItemId, Guid requestId, [AsParameters] OpenItemAdjustmentRequestReadinessQuery query, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var plan = await openItemRepository.GetAdjustmentRequestExecutionPlanAsync(
            new(query.CompanyId),
            openItemId,
            requestId,
            asOfDate,
            cancellationToken);

        return plan is null
            ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
            : Results.Ok(plan);
    });

accounting.MapPost(
    "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/execute",
    async (Guid openItemId, Guid requestId, ExecuteOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, PostApOpenItemAdjustmentCommandHandler handler, CancellationToken cancellationToken) =>
    {
        var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
        if (!actorId.HasValue)
        {
            return Results.BadRequest(new { message = "A user id is required to execute a governed AP open item adjustment." });
        }

        try
        {
            var result = await handler.HandleAsync(
                new PostApOpenItemAdjustmentCommand(
                    new(request.CompanyId),
                    openItemId,
                    requestId,
                    new(actorId.Value),
                    request.AdjustmentAccountId,
                    request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/documents/source",
    async (
        [AsParameters] SourceDocumentBrowserLookupQuery query,
        IAccountingDocumentReviewRepository repository,
        IBillReceiptMatchingRepository billReceiptMatchingRepository,
        IInventoryShipmentStore inventoryShipmentStore,
        CancellationToken cancellationToken) =>
    {
        var items = await repository.ListSourceDocumentsAsync(
            new(query.CompanyId),
            query.SourceType,
            query.CounterpartyRole,
            query.CounterpartyId,
            query.Limit ?? 100,
            cancellationToken);

        var billIds = items
            .Where(static item => string.Equals(item.SourceType, "bill", StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.Id)
            .Distinct()
            .ToArray();
        var billReceiptSummaries = await billReceiptMatchingRepository.GetBillPostingGateSnapshotsAsync(
            new(query.CompanyId),
            billIds,
            cancellationToken);
        var invoiceIds = items
            .Where(static item => string.Equals(item.SourceType, "invoice", StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.Id)
            .Distinct()
            .ToArray();
        var invoiceShipmentSummaries = await inventoryShipmentStore.GetInvoicePostingGateSnapshotsAsync(
            query.CompanyId,
            invoiceIds,
            cancellationToken);

        return Results.Ok(items.Select(item =>
        {
            billReceiptSummaries.TryGetValue(item.Id, out var receiptSummary);
            invoiceShipmentSummaries.TryGetValue(item.Id, out var shipmentSummary);
            return new
            {
                item.SourceType,
                SourceTypeLabel = MapDocumentReviewSourceLabel(item.SourceType),
                item.Id,
                CompanyId = item.CompanyId.Value,
                item.EntityNumber,
                item.DisplayNumber,
                item.Status,
                item.DocumentDate,
                item.DueDate,
                CounterpartyLabel = MapDocumentReviewCounterpartyLabel(item.CounterpartyRole),
                item.CounterpartyId,
                item.CounterpartyDisplayName,
                item.TransactionCurrencyCode,
                item.BaseCurrencyCode,
                item.TotalAmount,
                item.JournalEntryId,
                item.JournalEntryDisplayNumber,
                item.JournalEntryStatus,
                item.JournalEntryPostedAt,
                item.JournalEntryVoidedAt,
                item.JournalEntryReversedAt,
                BillReceiptMatchStatus = receiptSummary?.MatchStatus,
                BillReceiptPostingGateLabel = receiptSummary is null ? null : BillReceiptPostingGate.GetPostingGateLabel(receiptSummary),
                BillReceiptPostingGateSummary = receiptSummary is null ? null : BillReceiptPostingGate.GetPostingGateSummary(receiptSummary),
                BillReceiptAllowsPost = receiptSummary is null ? (bool?)null : BillReceiptPostingGate.AllowsBillPost(receiptSummary.MatchStatus),
                BillReceiptOpenDiscrepancyCount = receiptSummary?.OpenDiscrepancyCount,
                BillReceiptInvestigationSummary = receiptSummary is null ? null : BillReceiptDiscrepancyPolicy.BuildBrowserSummary(receiptSummary.OpenDiscrepancyCount),
                InvoiceShipmentMatchStatus = shipmentSummary?.MatchStatus,
                InvoiceShipmentPostingGateLabel = shipmentSummary is null ? null : ShipmentPostingGatePolicy.GetPostingGateLabel(shipmentSummary),
                InvoiceShipmentPostingGateSummary = shipmentSummary is null ? null : ShipmentPostingGatePolicy.GetPostingGateSummary(shipmentSummary),
                InvoiceShipmentAllowsPost = shipmentSummary is null ? (bool?)null : ShipmentPostingGatePolicy.AllowsInvoicePost(shipmentSummary.MatchStatus),
                InvoiceCoverageStatus = shipmentSummary?.InvoiceCoverageStatus,
                InvoiceCoverageSummary = shipmentSummary is null ? null : BuildInvoiceCoverageSummary(shipmentSummary)
            };
        }));
    });

accounting.MapGet(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}",
    async (
        string sourceType,
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var preview = await repository.GetLifecyclePreviewAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            cancellationToken);

        if (preview is null)
        {
            return Results.NotFound(new
            {
                message = "Source document lifecycle preview was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            preview.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(preview.SourceType),
            preview.Id,
            CompanyId = preview.CompanyId.Value,
            preview.EntityNumber,
            preview.DisplayNumber,
            preview.Status,
            preview.JournalEntryId,
            preview.JournalEntryDisplayNumber,
            preview.JournalEntryStatus,
            preview.JournalEntryPostedAt,
            preview.JournalEntryVoidedAt,
            preview.JournalEntryReversedAt,
            preview.LifecycleMode,
            preview.CanEditDraft,
            preview.CanPostDraft,
            preview.LifecycleReason,
            LifecycleActions = preview.LifecycleActions.Select(action => new
            {
                action.ActionCode,
                action.ActionLabel,
                action.AvailabilityMode,
                action.IsAvailable,
                action.Reason
            })
        });
    });

accounting.MapGet(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/actions/{actionCode}",
    async (
        string sourceType,
        Guid documentId,
        string actionCode,
        [AsParameters] DocumentReviewLookupQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var preview = await repository.GetLifecycleActionPreviewAsync(
                new(query.CompanyId),
                sourceType,
                documentId,
                actionCode,
                cancellationToken);

            if (preview is null)
            {
                return Results.NotFound(new
                {
                    message = "Source document lifecycle action preview was not found in the active company context."
                });
            }

            return Results.Ok(new
            {
                preview.SourceType,
                SourceTypeLabel = MapDocumentReviewSourceLabel(preview.SourceType),
                preview.Id,
                CompanyId = preview.CompanyId.Value,
                preview.EntityNumber,
                preview.DisplayNumber,
                preview.Status,
                preview.JournalEntryId,
                preview.JournalEntryDisplayNumber,
                preview.JournalEntryStatus,
                preview.JournalEntryPostedAt,
                preview.JournalEntryVoidedAt,
                preview.JournalEntryReversedAt,
                preview.LifecycleMode,
                preview.CanEditDraft,
                preview.CanPostDraft,
                preview.LifecycleReason,
                Action = new
                {
                    preview.ActionCode,
                    preview.ActionLabel,
                    preview.AvailabilityMode,
                    preview.IsAvailable,
                    preview.Reason
                }
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

accounting.MapPost(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/void",
    async (
        string sourceType,
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var attempt = await repository.AttemptVoidAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            cancellationToken);

        if (attempt is null)
        {
            return Results.NotFound(new
            {
                message = "Source document void attempt could not find the document in the active company context."
            });
        }

        var payload = new
        {
            attempt.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(attempt.SourceType),
            attempt.Id,
            CompanyId = attempt.CompanyId.Value,
            attempt.EntityNumber,
            attempt.DisplayNumber,
            attempt.Status,
            attempt.JournalEntryId,
            attempt.JournalEntryDisplayNumber,
            attempt.JournalEntryStatus,
            attempt.LifecycleMode,
            attempt.ActionCode,
            attempt.ActionLabel,
            attempt.AvailabilityMode,
            attempt.ExecutionMode,
            attempt.CommandAccepted,
            attempt.Executed,
            attempt.OutcomeCode,
            Message = attempt.Message
        };

        return attempt.OutcomeCode switch
        {
            "blocked" => Results.Conflict(payload),
            "not_implemented" => Results.Json(payload, statusCode: StatusCodes.Status501NotImplemented),
            "ready_for_implementation" => Results.Json(payload, statusCode: StatusCodes.Status501NotImplemented),
            _ => Results.Ok(payload)
        };
    });

accounting.MapPost(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse",
    async (
        string sourceType,
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        BusinessSessionContextAccessor sessionAccessor,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var actorId = sessionAccessor.Current?.UserId;
        var attempt = await repository.AttemptReverseAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            actorId,
            cancellationToken);

        if (attempt is null)
        {
            return Results.NotFound(new
            {
                message = "Source document reverse attempt could not find the document in the active company context."
            });
        }

        var payload = new
        {
            attempt.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(attempt.SourceType),
            attempt.Id,
            CompanyId = attempt.CompanyId.Value,
            attempt.EntityNumber,
            attempt.DisplayNumber,
            attempt.Status,
            attempt.JournalEntryId,
            attempt.JournalEntryDisplayNumber,
            attempt.JournalEntryStatus,
            attempt.LifecycleMode,
            attempt.ActionCode,
            attempt.ActionLabel,
            attempt.AvailabilityMode,
            attempt.ExecutionMode,
            attempt.CommandAccepted,
            attempt.Executed,
            attempt.RequestId,
            attempt.Persisted,
            attempt.OutcomeCode,
            Message = attempt.Message
        };

        return attempt.OutcomeCode switch
        {
            "blocked" => Results.Conflict(payload),
            "request_already_open" => Results.Conflict(payload),
            "request_recorded" => Results.Json(payload, statusCode: StatusCodes.Status202Accepted),
            _ => Results.Ok(payload)
        };
    });

accounting.MapGet(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request",
    async (
        string sourceType,
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var request = await repository.GetLatestReverseRequestAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            cancellationToken);

        if (request is null)
        {
            return Results.NotFound(new
            {
                message = "No reverse request has been recorded for this source document in the active company context."
            });
        }

        return Results.Ok(new
        {
            request.RequestId,
            CompanyId = request.CompanyId.Value,
            request.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
            Id = request.DocumentId,
            request.EntityNumber,
            request.DisplayNumber,
            request.Status,
            request.JournalEntryId,
            request.JournalEntryDisplayNumber,
            request.JournalEntryStatus,
            request.LifecycleMode,
            request.ActionCode,
            request.ActionLabel,
            request.AvailabilityMode,
            request.IsAvailable,
            request.Reason,
            request.RequestStatus,
            RequestedByActorType = request.RequestedByActorType,
            RequestedByActorId = request.RequestedByActorId,
            request.RequestedAt,
            SubmittedByActorType = request.SubmittedByActorType,
            SubmittedByActorId = request.SubmittedByActorId,
            request.SubmittedAt,
            CancelledByActorType = request.CancelledByActorType,
            CancelledByActorId = request.CancelledByActorId,
            request.CancelledAt,
            request.ExecutionStatus,
            ExecutionRequestedByActorType = request.ExecutionRequestedByActorType,
            ExecutionRequestedByActorId = request.ExecutionRequestedByActorId,
            request.ExecutionRequestedAt,
            ExecutionCompletedByActorType = request.ExecutionCompletedByActorType,
            ExecutionCompletedByActorId = request.ExecutionCompletedByActorId,
            request.ExecutionCompletedAt,
            request.CompensationJournalEntryId,
            request.CompensationJournalEntryDisplayNumber,
            request.CompensationSourceType
        });
    });

accounting.MapGet(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-blockers",
    async (
        string sourceType,
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var blockers = await repository.ListSubledgerReverseBlockersAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            cancellationToken);

        return Results.Ok(blockers.Select(blocker => new
        {
            blocker.SettlementApplicationId,
            blocker.ApplicationType,
            blocker.SettlementSourceType,
            SettlementSourceTypeLabel = MapDocumentReviewSourceLabel(blocker.SettlementSourceType),
            blocker.SettlementSourceId,
            blocker.SettlementSourceDisplayNumber,
            blocker.SettlementSourceDocumentDate,
            blocker.TargetOpenItemType,
            blocker.TargetOpenItemId,
            blocker.TargetSourceType,
            TargetSourceTypeLabel = MapDocumentReviewSourceLabel(blocker.TargetSourceType),
            blocker.TargetSourceId,
            blocker.TargetSourceDisplayNumber,
            blocker.AppliedAmountTx,
            blocker.AppliedAmountBase,
            blocker.SettlementFxRate,
            blocker.RealizedFxAmount,
            blocker.AppliedAt,
            blocker.ReverseRequestId,
            blocker.ReverseRequestStatus,
            blocker.ReverseExecutionStatus,
            blocker.ReverseRequestedAt
        }));
    });

accounting.MapGet(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/settlement-application-reversals",
    async (
        string sourceType,
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var reversals = await repository.ListSettlementApplicationReversalsAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            cancellationToken);

        return Results.Ok(reversals.Select(reversal => new
        {
            reversal.ReversalEventId,
            reversal.RequestId,
            reversal.SettlementApplicationId,
            reversal.ApplicationType,
            reversal.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(reversal.SourceType),
            reversal.SourceId,
            reversal.TargetOpenItemType,
            reversal.TargetOpenItemId,
            reversal.AppliedAmountTx,
            reversal.AppliedAmountBase,
            reversal.SettlementFxRate,
            reversal.RealizedFxAmount,
            reversal.OriginalApplicationCreatedAt,
            reversal.OriginalApplicationCreatedByUserId,
            reversal.ReversedAt,
            reversal.ReversedByActorType,
            reversal.ReversedByActorId,
            reversal.ReversalMode
        }));
    });

accounting.MapPost(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/submit",
    async (
        string sourceType,
        Guid documentId,
        Guid requestId,
        [AsParameters] DocumentReviewLookupQuery query,
        BusinessSessionContextAccessor sessionAccessor,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var actorId = sessionAccessor.Current?.UserId;
        var result = await repository.SubmitReverseRequestAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            requestId,
            actorId,
            cancellationToken);

        if (result is null)
        {
            return Results.NotFound(new
            {
                message = "Reverse request could not be found in the active company context."
            });
        }

        var request = result.Request;
        var payload = new
        {
            result.TransitionCode,
            result.OutcomeCode,
            Message = result.Message,
            request.RequestId,
            CompanyId = request.CompanyId.Value,
            request.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
            Id = request.DocumentId,
            request.EntityNumber,
            request.DisplayNumber,
            request.Status,
            request.JournalEntryId,
            request.JournalEntryDisplayNumber,
            request.JournalEntryStatus,
            request.LifecycleMode,
            request.ActionCode,
            request.ActionLabel,
            request.AvailabilityMode,
            request.IsAvailable,
            request.Reason,
            request.RequestStatus,
            RequestedByActorType = request.RequestedByActorType,
            RequestedByActorId = request.RequestedByActorId,
            request.RequestedAt,
            SubmittedByActorType = request.SubmittedByActorType,
            SubmittedByActorId = request.SubmittedByActorId,
            request.SubmittedAt,
            CancelledByActorType = request.CancelledByActorType,
            CancelledByActorId = request.CancelledByActorId,
            request.CancelledAt,
            request.ExecutionStatus,
            ExecutionRequestedByActorType = request.ExecutionRequestedByActorType,
            ExecutionRequestedByActorId = request.ExecutionRequestedByActorId,
            request.ExecutionRequestedAt
        };

        return result.OutcomeCode switch
        {
            "submitted" => Results.Ok(payload),
            _ => Results.Conflict(payload)
        };
    });

accounting.MapPost(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/cancel",
    async (
        string sourceType,
        Guid documentId,
        Guid requestId,
        [AsParameters] DocumentReviewLookupQuery query,
        BusinessSessionContextAccessor sessionAccessor,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var actorId = sessionAccessor.Current?.UserId;
        var result = await repository.CancelReverseRequestAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            requestId,
            actorId,
            cancellationToken);

        if (result is null)
        {
            return Results.NotFound(new
            {
                message = "Reverse request could not be found in the active company context."
            });
        }

        var request = result.Request;
        var payload = new
        {
            result.TransitionCode,
            result.OutcomeCode,
            Message = result.Message,
            request.RequestId,
            CompanyId = request.CompanyId.Value,
            request.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
            Id = request.DocumentId,
            request.EntityNumber,
            request.DisplayNumber,
            request.Status,
            request.JournalEntryId,
            request.JournalEntryDisplayNumber,
            request.JournalEntryStatus,
            request.LifecycleMode,
            request.ActionCode,
            request.ActionLabel,
            request.AvailabilityMode,
            request.IsAvailable,
            request.Reason,
            request.RequestStatus,
            RequestedByActorType = request.RequestedByActorType,
            RequestedByActorId = request.RequestedByActorId,
            request.RequestedAt,
            SubmittedByActorType = request.SubmittedByActorType,
            SubmittedByActorId = request.SubmittedByActorId,
            request.SubmittedAt,
            CancelledByActorType = request.CancelledByActorType,
            CancelledByActorId = request.CancelledByActorId,
            request.CancelledAt,
            request.ExecutionStatus,
            ExecutionRequestedByActorType = request.ExecutionRequestedByActorType,
            ExecutionRequestedByActorId = request.ExecutionRequestedByActorId,
            request.ExecutionRequestedAt
        };

        return result.OutcomeCode switch
        {
            "cancelled" => Results.Ok(payload),
            _ => Results.Conflict(payload)
        };
    });

accounting.MapGet(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/apply-readiness",
    async (
        string sourceType,
        Guid documentId,
        Guid requestId,
        [AsParameters] DocumentLifecycleRequestReadinessQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var readiness = await repository.GetReverseRequestApplyReadinessAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            requestId,
            asOfDate,
            cancellationToken);

        if (readiness is null)
        {
            return Results.NotFound(new
            {
                message = "Reverse request could not be found in the active company context."
            });
        }

        var request = readiness.Request;
        return Results.Ok(new
        {
            request.RequestId,
            CompanyId = request.CompanyId.Value,
            request.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
            Id = request.DocumentId,
            request.EntityNumber,
            request.DisplayNumber,
            request.Status,
            request.RequestStatus,
            request.LifecycleMode,
            AsOfDate = readiness.AsOfDate,
            readiness.GovernanceReady,
            readiness.ApplyReady,
            readiness.ExecutionMode,
            readiness.AvailabilityMode,
            readiness.IsAvailable,
            readiness.Reason
        });
    });

accounting.MapPost(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/execute",
    async (
        string sourceType,
        Guid documentId,
        Guid requestId,
        [AsParameters] DocumentLifecycleRequestReadinessQuery query,
        BusinessSessionContextAccessor sessionAccessor,
        IAccountingDocumentReviewRepository repository,
        GlIJournalEntryLifecycleWorkflow journalEntryLifecycleWorkflow,
        CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var actorId = sessionAccessor.Current?.UserId;
        var result = await repository.ExecuteReverseRequestAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            requestId,
            actorId,
            asOfDate,
            cancellationToken);

        if (result is null)
        {
            return Results.NotFound(new
            {
                message = "Reverse request could not be found in the active company context."
            });
        }

        var request = result.Request;
        var shouldRunLinkedJournalEntryReverse =
            request.JournalEntryId.HasValue &&
            string.Equals(request.ExecutionStatus, "execution_requested", StringComparison.Ordinal) &&
            request.ExecutionCompletedAt is null;

        if (shouldRunLinkedJournalEntryReverse)
        {
            if (!actorId.HasValue)
            {
                return Results.BadRequest(new
                {
                    message = "A business-session user is required before governed reverse execution can reverse the linked journal entry."
                });
            }

            try
            {
                var lifecycleResult = await journalEntryLifecycleWorkflow.ReverseAsync(
                    query.CompanyId,
                    request.JournalEntryId!.Value,
                    actorId.Value,
                    cancellationToken);

                result = await repository.CompleteReverseRequestExecutionAsync(
                        new(query.CompanyId),
                        sourceType,
                        documentId,
                        requestId,
                        actorId,
                        lifecycleResult.CompensationJournalEntryId,
                        lifecycleResult.CompensationDisplayNumber,
                        lifecycleResult.CompensationSourceType,
                        lifecycleResult.LifecycleAt,
                        cancellationToken)
                    ?? result;

                request = result.Request;
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new
                {
                    code = ResolveAccountingOperationErrorCode(ex.Message),
                    request.RequestId,
                    CompanyId = request.CompanyId.Value,
                    request.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
                    Id = request.DocumentId,
                    request.EntityNumber,
                    request.DisplayNumber,
                    request.Status,
                    request.RequestStatus,
                    request.ExecutionStatus,
                    AsOfDate = asOfDate,
                    ExecutionMode = "governed_execution_orchestration",
                    Message = ex.Message
                });
            }
        }

        var payload = new
        {
            request.RequestId,
            CompanyId = request.CompanyId.Value,
            request.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
            Id = request.DocumentId,
            request.EntityNumber,
            request.DisplayNumber,
            request.Status,
            request.RequestStatus,
            request.ExecutionStatus,
            AsOfDate = result.AsOfDate,
            result.ExecutionMode,
            result.CommandAccepted,
            result.Executed,
            result.Persisted,
            result.OutcomeCode,
            Message = result.Message,
            ExecutionRequestedByActorType = request.ExecutionRequestedByActorType,
            ExecutionRequestedByActorId = request.ExecutionRequestedByActorId,
            request.ExecutionRequestedAt,
            ExecutionCompletedByActorType = request.ExecutionCompletedByActorType,
            ExecutionCompletedByActorId = request.ExecutionCompletedByActorId,
            request.ExecutionCompletedAt,
            request.CompensationJournalEntryId,
            request.CompensationJournalEntryDisplayNumber,
            request.CompensationSourceType
        };

        return result.OutcomeCode switch
        {
            "blocked" or "blocked_by_subledger_truth" or "blocked_by_missing_linked_journal_entry" => Results.BadRequest(payload),
            "execution_already_requested" or "execution_already_completed" => Results.Conflict(payload),
            "execution_request_recorded" => Results.Json(payload, statusCode: StatusCodes.Status202Accepted),
            _ => Results.Ok(payload)
        };
    });

accounting.MapGet(
    "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/execution-plan",
    async (
        string sourceType,
        Guid documentId,
        Guid requestId,
        [AsParameters] DocumentLifecycleRequestReadinessQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var plan = await repository.GetReverseRequestExecutionPlanAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            requestId,
            asOfDate,
            cancellationToken);

        if (plan is null)
        {
            return Results.NotFound(new
            {
                message = "Reverse request could not be found in the active company context."
            });
        }

        var request = plan.Request;
        return Results.Ok(new
        {
            request.RequestId,
            CompanyId = request.CompanyId.Value,
            request.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
            Id = request.DocumentId,
            request.EntityNumber,
            request.DisplayNumber,
            request.Status,
            request.RequestStatus,
            request.ExecutionStatus,
            request.LifecycleMode,
            AsOfDate = plan.AsOfDate,
            plan.ExecutionMode,
            plan.CanExecute,
            plan.OverallStatus,
            plan.Reason,
            Steps = plan.Steps.Select(step => new
            {
                step.StepNumber,
                step.StepCode,
                step.StepLabel,
                step.StepStatus,
                step.Reason
            })
        });
    });

accounting.MapGet(
    "/document-review/{sourceType}/{documentId:guid}",
    async (
        string sourceType,
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var review = await repository.GetSourceDocumentAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            cancellationToken);

        if (review is null)
        {
            return Results.NotFound(new
            {
                message = "Source document review was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            review.SourceType,
            SourceTypeLabel = MapDocumentReviewSourceLabel(review.SourceType),
            review.Id,
            CompanyId = review.CompanyId.Value,
            review.EntityNumber,
            review.DisplayNumber,
            review.Status,
            review.DocumentDate,
            review.DueDate,
            CounterpartyLabel = MapDocumentReviewCounterpartyLabel(review.CounterpartyRole),
            review.CounterpartyId,
            ControlAccountLabel = MapDocumentReviewControlAccountLabel(review.CounterpartyRole),
            review.ControlAccountId,
            review.JournalEntryId,
            review.JournalEntryDisplayNumber,
            review.JournalEntryStatus,
            review.JournalEntryPostedAt,
            review.JournalEntryVoidedAt,
            review.JournalEntryReversedAt,
            review.LifecycleMode,
            review.CanEditDraft,
            review.CanPostDraft,
            review.LifecycleReason,
            LifecycleActions = review.LifecycleActions.Select(action => new
            {
                action.ActionCode,
                action.ActionLabel,
                action.AvailabilityMode,
                action.IsAvailable,
                action.Reason
            }),
            review.TransactionCurrencyCode,
            review.BaseCurrencyCode,
            review.SubtotalAmount,
            review.TaxAmount,
            review.TotalAmount,
            review.Memo,
            Lines = review.Lines.Select(line => new
            {
                line.LineNumber,
                line.AccountId,
                line.AccountCode,
                line.AccountName,
                AccountLabel = MapDocumentReviewLineAccountLabel(review.CounterpartyRole),
                line.Description,
                line.Quantity,
                line.UnitPrice,
                line.LineAmount,
                line.TaxAmount,
                line.IsTaxRecoverable,
                line.TaxAccountId,
                line.TxDebit,
                line.TxCredit,
                line.SourceOpenItemId,
                line.SourceDocumentType,
                line.SourceDocumentId,
                line.SourceDocumentDisplayNumber,
                line.TargetOpenItemId,
                line.TargetDocumentType,
                line.TargetDocumentId,
                line.TargetDocumentDisplayNumber
            })
        });
    });

// ---------------------------------------------------------------------------
// Invoice PDF download (Batch 1 of the invoice send / template work).
//
// Returns the invoice as a PDF byte stream rendered through QuestPDF using a
// fixed "default" template. The HTML invoice preview that lands in Batch 4
// will share the same InvoiceRenderModel + builder so the bytes the user
// downloads always match the on-screen preview. Subsequent batches will
// thread an InvoiceTemplate snapshot through here for branding overrides.
// ---------------------------------------------------------------------------
accounting.MapGet(
    "/document-review/invoice/{documentId:guid}/pdf",
    async (
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        Guid? templateId,
        IAccountingDocumentReviewRepository reviewRepository,
        ICustomerStore customerStore,
        ICompanyProfileQuery companyProfileQuery,
        IInvoiceTemplateStore templateStore,
        IInvoicePdfRenderer renderer,
        CancellationToken cancellationToken) =>
    {
        var companyId = new CompanyId(query.CompanyId);

        var review = await reviewRepository.GetSourceDocumentAsync(
            companyId,
            "invoice",
            documentId,
            cancellationToken);

        if (review is null)
        {
            return Results.NotFound(new
            {
                message = "Invoice was not found in the active company context."
            });
        }

        var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
        if (company is null)
        {
            return Results.NotFound(new
            {
                message = "Company profile is not provisioned. Run the SysAdmin First-Company Wizard before downloading invoice PDFs."
            });
        }

        CustomerRecord? customer = null;
        if (review.CounterpartyId is { } counterpartyId)
        {
            customer = await customerStore.GetByIdAsync(query.CompanyId, counterpartyId, cancellationToken);
        }

        // Optional ?templateId override lets the template editor's
        // "Download sample" button preview an unsaved draft against a
        // real invoice. Default path uses the company's default template.
        InvoiceTemplate? template = null;
        if (templateId is { } overrideId)
        {
            template = await templateStore.GetByIdAsync(query.CompanyId, overrideId, cancellationToken);
        }
        template ??= await templateStore.GetDefaultAsync(query.CompanyId, cancellationToken);

        var projection = new InvoiceReviewProjection(
            DisplayNumber: review.DisplayNumber,
            EntityNumber: review.EntityNumber,
            DocumentDate: review.DocumentDate,
            DueDate: review.DueDate,
            Status: review.Status,
            CounterpartyDisplayName: customer?.DisplayName,
            TransactionCurrencyCode: review.TransactionCurrencyCode,
            SubtotalAmount: review.SubtotalAmount,
            TaxAmount: review.TaxAmount,
            TotalAmount: review.TotalAmount,
            Memo: review.Memo,
            Lines: review.Lines.Select(line => new InvoiceReviewLineProjection(
                LineNumber: line.LineNumber,
                Description: line.Description,
                Quantity: line.Quantity,
                UnitPrice: line.UnitPrice,
                LineAmount: line.LineAmount,
                TaxAmount: line.TaxAmount)).ToArray());

        var renderModel = InvoiceRenderModelBuilder.Build(projection, company, customer, template?.Config);
        var pdfBytes = renderer.Render(renderModel);

        return Results.File(
            fileContents: pdfBytes,
            contentType: "application/pdf",
            fileDownloadName: $"{review.DisplayNumber}.pdf");
    });

// ---------------------------------------------------------------------------
// Send invoice by email (Batch 2). Composes subject + HTML body + plain-text
// body from the InvoiceRenderModel + an optional operator-typed note,
// renders the same PDF the Download-PDF endpoint serves, ships through the
// platform's SMTP options, and writes one row to invoice_send_history
// regardless of outcome (so audit always captures both successful and
// failed sends).
// ---------------------------------------------------------------------------
accounting.MapPost(
    "/document-review/invoice/{documentId:guid}/send",
    async (
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        InvoiceSendHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IAccountingDocumentReviewRepository reviewRepository,
        ICustomerStore customerStore,
        ICompanyProfileQuery companyProfileQuery,
        IInvoiceTemplateStore templateStore,
        IInvoicePdfRenderer renderer,
        IInvoiceEmailSender emailSender,
        IInvoiceSendHistoryStore historyStore,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.ToEmail) ||
            !request.ToEmail.Contains('@', StringComparison.Ordinal))
        {
            return Results.BadRequest(new { message = "A recipient email is required." });
        }

        var companyId = new CompanyId(query.CompanyId);

        var review = await reviewRepository.GetSourceDocumentAsync(
            companyId,
            "invoice",
            documentId,
            cancellationToken);
        if (review is null)
        {
            return Results.NotFound(new { message = "Invoice not found in the active company context." });
        }

        var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
        if (company is null)
        {
            return Results.NotFound(new { message = "Company profile is not provisioned." });
        }

        CustomerRecord? customer = null;
        if (review.CounterpartyId is { } counterpartyId)
        {
            customer = await customerStore.GetByIdAsync(query.CompanyId, counterpartyId, cancellationToken);
        }

        var template = await templateStore.GetDefaultAsync(query.CompanyId, cancellationToken);

        var projection = new InvoiceReviewProjection(
            DisplayNumber: review.DisplayNumber,
            EntityNumber: review.EntityNumber,
            DocumentDate: review.DocumentDate,
            DueDate: review.DueDate,
            Status: review.Status,
            CounterpartyDisplayName: customer?.DisplayName,
            TransactionCurrencyCode: review.TransactionCurrencyCode,
            SubtotalAmount: review.SubtotalAmount,
            TaxAmount: review.TaxAmount,
            TotalAmount: review.TotalAmount,
            Memo: review.Memo,
            Lines: review.Lines.Select(line => new InvoiceReviewLineProjection(
                LineNumber: line.LineNumber,
                Description: line.Description,
                Quantity: line.Quantity,
                UnitPrice: line.UnitPrice,
                LineAmount: line.LineAmount,
                TaxAmount: line.TaxAmount)).ToArray());

        var renderModel = InvoiceRenderModelBuilder.Build(projection, company, customer, template?.Config);
        var pdfBytes = renderer.Render(renderModel);
        var composition = InvoiceEmailComposer.Compose(
            renderModel,
            request.Message,
            subjectTemplate: template?.Config.EmailSubjectTemplate);

        var ccList = SplitEmailList(request.Cc);
        var bccList = SplitEmailList(request.Bcc);

        var emailRequest = new InvoiceEmailRequest(
            ToEmail: request.ToEmail.Trim(),
            ToDisplayName: customer?.DisplayName ?? string.Empty,
            CcEmails: ccList,
            BccEmails: bccList,
            Subject: composition.Subject,
            HtmlBody: composition.HtmlBody,
            PlainTextBody: composition.PlainTextBody,
            AttachmentFileName: $"{review.DisplayNumber}.pdf",
            AttachmentBytes: pdfBytes);

        var sendResult = await emailSender.SendAsync(emailRequest, cancellationToken);

        var historyRecord = await historyStore.RecordAsync(
            new InvoiceSendHistoryDraft(
                CompanyId: query.CompanyId,
                InvoiceId: documentId,
                SentByUserId: session.UserId,
                ToEmail: emailRequest.ToEmail,
                CcEmails: string.Join(", ", ccList),
                BccEmails: string.Join(", ", bccList),
                Subject: composition.Subject,
                Status: sendResult.Succeeded ? "sent" : "failed",
                ErrorMessage: sendResult.ErrorMessage),
            cancellationToken);

        if (!sendResult.Succeeded)
        {
            return Results.UnprocessableEntity(new
            {
                succeeded = false,
                message = sendResult.ErrorMessage ?? "Email delivery failed.",
                historyId = historyRecord.Id,
                sentAt = historyRecord.SentAt,
            });
        }

        return Results.Ok(new
        {
            succeeded = true,
            historyId = historyRecord.Id,
            sentAt = historyRecord.SentAt,
            toEmail = historyRecord.ToEmail,
            subject = historyRecord.Subject,
        });
    });

// ---------------------------------------------------------------------------
// Read-only view of the invoice's send history. Powers the "Last sent"
// badge on the document detail page and (later) the timeline panel that
// exposes failed attempts for re-send.
// ---------------------------------------------------------------------------
accounting.MapGet(
    "/document-review/invoice/{documentId:guid}/send-history",
    async (
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        IInvoiceSendHistoryStore historyStore,
        CancellationToken cancellationToken) =>
    {
        var rows = await historyStore.ListByInvoiceAsync(
            query.CompanyId,
            documentId,
            limit: 50,
            cancellationToken);

        return Results.Ok(rows.Select(r => new
        {
            r.Id,
            r.SentAt,
            r.SentByUserId,
            r.ToEmail,
            r.CcEmails,
            r.BccEmails,
            r.Subject,
            r.Status,
            r.ErrorMessage,
        }).ToArray());
    });

// ---------------------------------------------------------------------------
// Invoice templates (Batch 3). One per-company table with three lazy-
// seeded starters (Modern / Classic / Minimal). The default flows
// through every PDF / email send. Endpoints:
//   GET  /invoice-templates                       -> list
//   GET  /invoice-templates/{id}                  -> single
//   POST /invoice-templates                       -> create (empty -> default config copy)
//   PUT  /invoice-templates/{id}                  -> update name + config
//   POST /invoice-templates/{id}/set-default      -> mark as default (single transaction)
// ---------------------------------------------------------------------------
accounting.MapGet(
    "/invoice-templates",
    async (
        [AsParameters] DocumentReviewLookupQuery query,
        IInvoiceTemplateStore store,
        CancellationToken cancellationToken) =>
    {
        var templates = await store.ListByCompanyAsync(query.CompanyId, cancellationToken);
        return Results.Ok(templates.Select(MapInvoiceTemplate).ToArray());
    });

accounting.MapGet(
    "/invoice-templates/{templateId:guid}",
    async (
        Guid templateId,
        [AsParameters] DocumentReviewLookupQuery query,
        IInvoiceTemplateStore store,
        CancellationToken cancellationToken) =>
    {
        var template = await store.GetByIdAsync(query.CompanyId, templateId, cancellationToken);
        return template is null
            ? Results.NotFound(new { message = "Invoice template not found in this company." })
            : Results.Ok(MapInvoiceTemplate(template));
    });

accounting.MapPost(
    "/invoice-templates",
    async (
        [AsParameters] DocumentReviewLookupQuery query,
        InvoiceTemplateUpsertHttpRequest request,
        IInvoiceTemplateStore store,
        CancellationToken cancellationToken) =>
    {
        var (config, validationError) = TryReadInvoiceTemplateConfig(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new { message = validationError });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { message = "Template name is required." });
        }

        var created = await store.CreateAsync(
            query.CompanyId,
            new InvoiceTemplateUpsertRequest(request.Name.Trim(), config),
            cancellationToken);

        return Results.Created(
            $"/accounting/invoice-templates/{created.Id:D}?companyId={query.CompanyId:D}",
            MapInvoiceTemplate(created));
    });

accounting.MapPut(
    "/invoice-templates/{templateId:guid}",
    async (
        Guid templateId,
        [AsParameters] DocumentReviewLookupQuery query,
        InvoiceTemplateUpsertHttpRequest request,
        IInvoiceTemplateStore store,
        CancellationToken cancellationToken) =>
    {
        var (config, validationError) = TryReadInvoiceTemplateConfig(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new { message = validationError });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { message = "Template name is required." });
        }

        var updated = await store.UpdateAsync(
            query.CompanyId,
            templateId,
            new InvoiceTemplateUpsertRequest(request.Name.Trim(), config),
            cancellationToken);

        return updated is null
            ? Results.NotFound(new { message = "Invoice template not found in this company." })
            : Results.Ok(MapInvoiceTemplate(updated));
    });

accounting.MapPost(
    "/invoice-templates/{templateId:guid}/set-default",
    async (
        Guid templateId,
        [AsParameters] DocumentReviewLookupQuery query,
        IInvoiceTemplateStore store,
        CancellationToken cancellationToken) =>
    {
        var defaulted = await store.SetDefaultAsync(query.CompanyId, templateId, cancellationToken);
        return defaulted is null
            ? Results.NotFound(new { message = "Invoice template not found in this company." })
            : Results.Ok(MapInvoiceTemplate(defaulted));
    });

// ---------------------------------------------------------------------------
// Renders a PDF preview of the *draft* template (the unsaved upsert body),
// so the Settings editor can show a byte-accurate "what your customer
// sees" iframe that updates as the operator types. Uses the issuing
// company's profile as the issuer block and a hard-coded sample invoice
// (Acme Co. / two demo lines) as the bill-to + lines so the preview
// works even before any real invoice exists.
// ---------------------------------------------------------------------------
accounting.MapPost(
    "/invoice-templates/preview-pdf",
    async (
        [AsParameters] DocumentReviewLookupQuery query,
        InvoiceTemplateUpsertHttpRequest request,
        ICompanyProfileQuery companyProfileQuery,
        IInvoicePdfRenderer renderer,
        CancellationToken cancellationToken) =>
    {
        var (config, validationError) = TryReadInvoiceTemplateConfig(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new { message = validationError });
        }

        var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
        if (company is null)
        {
            return Results.NotFound(new { message = "Company profile is not provisioned." });
        }

        var sample = BuildSampleInvoiceProjection(company.BaseCurrencyCode);
        var renderModel = InvoiceRenderModelBuilder.Build(sample, company, customer: null, config);
        var pdfBytes = renderer.Render(renderModel);

        return Results.File(pdfBytes, "application/pdf");
    });

accounting.MapGet(
    "/journal-entries",
    async (
        [AsParameters] JournalEntryListLookupQuery query,
        IJournalEntryReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var items = await repository.ListRecentAsync(
            new(query.CompanyId),
            query.Take,
            cancellationToken);

        return Results.Ok(items.Select(MapJournalEntryReviewListItem).ToArray());
    });

accounting.MapGet(
    "/journal-entries/by-source/{sourceType}/{sourceId:guid}",
    async (
        string sourceType,
        Guid sourceId,
        [AsParameters] JournalEntryLookupQuery query,
        IJournalEntryReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var item = await repository.FindBySourceAsync(
            new(query.CompanyId),
            sourceType,
            sourceId,
            cancellationToken);

        if (item is null)
        {
            return Results.NotFound(new
            {
                message = "Journal entry was not found for the requested source document in the active company context."
            });
        }

        return Results.Ok(MapJournalEntryReviewListItem(item));
    });

accounting.MapGet(
    "/journal-entries/{journalEntryId:guid}",
    async (
        Guid journalEntryId,
        [AsParameters] JournalEntryLookupQuery query,
        IJournalEntryReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var review = await repository.GetAsync(
            new(query.CompanyId),
            journalEntryId,
            cancellationToken);

        if (review is null)
        {
            return Results.NotFound(new
            {
                message = "Journal entry was not found in the active company context."
            });
        }

        return Results.Ok(MapJournalEntryReview(review));
    });

accounting.MapGet(
    "/unity-search",
    async (
        [AsParameters] UnitySearchHttpQuery query,
        BusinessSessionContextAccessor sessionAccessor,
        IUnitySearchEngine engine,
        CancellationToken cancellationToken) =>
    {
        var result = await engine.SearchAsync(
            new UnitySearchQuery
            {
                CompanyId = query.CompanyId,
                UserId = query.UserId ?? sessionAccessor.Current?.UserId,
                Context = string.IsNullOrWhiteSpace(query.Context) ? Citus.Modules.UnitySearch.Domain.Shared.SearchScopeContext.GlobalTopbar : query.Context.Trim(),
                SearchText = query.Query ?? string.Empty,
                Take = query.Take ?? 10
            },
            cancellationToken);

        return Results.Ok(result);
    });

accounting.MapGet(
    "/unity-search/recent",
    async (
        [AsParameters] UnitySearchRecentHttpQuery query,
        BusinessSessionContextAccessor sessionAccessor,
        IUnitySearchEngine engine,
        CancellationToken cancellationToken) =>
    {
        var userId = query.UserId ?? sessionAccessor.Current?.UserId;
        if (!userId.HasValue || userId == Guid.Empty)
        {
            return Results.Ok(Array.Empty<UnitySearchRecentQueryRecord>());
        }

        var results = await engine.ListRecentQueriesAsync(
            query.CompanyId,
            userId.Value,
            string.IsNullOrWhiteSpace(query.Context) ? Citus.Modules.UnitySearch.Domain.Shared.SearchScopeContext.GlobalTopbar : query.Context.Trim(),
            query.Take ?? 10,
            cancellationToken);

        return Results.Ok(results);
    });

accounting.MapGet(
    "/unity-search/recent-selections",
    async (
        [AsParameters] UnitySearchRecentHttpQuery query,
        BusinessSessionContextAccessor sessionAccessor,
        IUnitySearchEngine engine,
        CancellationToken cancellationToken) =>
    {
        var userId = query.UserId ?? sessionAccessor.Current?.UserId;
        if (!userId.HasValue || userId == Guid.Empty)
        {
            return Results.Ok(Array.Empty<UnitySearchRecentSelectionRecord>());
        }

        var results = await engine.ListRecentSelectionsAsync(
            query.CompanyId,
            userId.Value,
            string.IsNullOrWhiteSpace(query.Context) ? Citus.Modules.UnitySearch.Domain.Shared.SearchScopeContext.GlobalTopbar : query.Context.Trim(),
            query.Take ?? 8,
            cancellationToken);

        return Results.Ok(results);
    });

accounting.MapPost(
    "/unity-search/clicks",
    async (
        UnitySearchClickHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IUnitySearchEngine engine,
        CancellationToken cancellationToken) =>
    {
        var userId = request.UserId == Guid.Empty ? sessionAccessor.Current?.UserId : request.UserId;
        if (!userId.HasValue || userId == Guid.Empty)
        {
            return Results.Accepted();
        }

        await engine.RecordClickAsync(
            request.CompanyId,
            userId.Value,
            string.IsNullOrWhiteSpace(request.Context) ? Citus.Modules.UnitySearch.Domain.Shared.SearchScopeContext.GlobalTopbar : request.Context.Trim(),
            request.EntityType,
            request.SourceId,
            cancellationToken);

        return Results.Accepted();
    });

// ===========================================================================
// unityAI V1 endpoints
//
// Authority: AI_PRODUCT_ARCHITECTURE.md
// Each endpoint is a thin shell over the unityAI Application services.
// Company isolation: every payload's CompanyId is checked against the
// authenticated session before any store call. Errors are non-fatal —
// usage tracking failures must not break the user's primary flow.
// ===========================================================================

// ===========================================================================
// Per-user profile (auth/me)
//
// V1 surface: GET the merged profile for the current user, POST to update
// the display-name override. Password change is intentionally not wired —
// bootstrap sessions have no password storage. The Profile UI shows a
// pending toast for password until a real auth backend ships.
// ===========================================================================

accounting.MapGet(
    "/auth/me/profile",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IUserProfileOverrideStore overrides,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.UserId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        var record = await overrides.GetByUserIdAsync(session.UserId, cancellationToken);
        return Results.Ok(new
        {
            userId = session.UserId,
            displayName = record?.DisplayName,
            updatedAt = record?.UpdatedAt,
        });
    });

accounting.MapPost(
    "/auth/me/display-name",
    async (
        UpdateDisplayNameHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IUserProfileOverrideStore overrides,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.UserId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        var trimmed = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return Results.BadRequest(new { message = "Display name is required." });
        }

        if (trimmed.Length > 120)
        {
            return Results.BadRequest(new { message = "Display name must be 120 characters or fewer." });
        }

        var saved = await overrides.UpsertDisplayNameAsync(session.UserId, trimmed, cancellationToken);

        return Results.Ok(new
        {
            userId = saved.UserId,
            displayName = saved.DisplayName,
            updatedAt = saved.UpdatedAt,
        });
    });

// ===========================================================================
// Tax codes (per-company catalog)
//
// V1 surface: list / create / update / activate-toggle. Backs the
// Settings → Tax Rates page and the per-line Sales Tax dropdowns.
// Posting-Engine consumers read the same tax_codes table directly; the
// store fills migration-draft columns (entity_number,
// recoverability_mode, account refs) with safe defaults when the V1 UI
// does not expose them yet.
// ===========================================================================

accounting.MapGet(
    "/tax-codes",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        ITaxCodeStore store,
        bool? includeInactive,
        string? appliesTo,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        var rows = await store.ListAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
        if (!string.IsNullOrWhiteSpace(appliesTo))
        {
            // applies_to=sales also surfaces 'both'; same for purchase.
            var wanted = appliesTo.Trim().ToLowerInvariant();
            rows = wanted switch
            {
                TaxCodeAppliesTo.Sales => rows.Where(r => r.AppliesTo is TaxCodeAppliesTo.Sales or TaxCodeAppliesTo.Both).ToArray(),
                TaxCodeAppliesTo.Purchase => rows.Where(r => r.AppliesTo is TaxCodeAppliesTo.Purchase or TaxCodeAppliesTo.Both).ToArray(),
                TaxCodeAppliesTo.Both => rows,
                _ => rows,
            };
        }
        return Results.Ok(rows);
    });

accounting.MapPost(
    "/tax-codes",
    async (
        TaxCodeUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ITaxCodeStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        var validation = ValidateTaxCodeInput(request);
        if (validation is not null)
        {
            return Results.BadRequest(new { message = validation });
        }

        try
        {
            var record = await store.CreateAsync(
                session.ActiveCompanyId,
                new TaxCodeUpsertInput(
                    Code: request.Code!.Trim(),
                    Name: request.Name!.Trim(),
                    RatePercent: request.RatePercent ?? 0m,
                    AppliesTo: request.AppliesTo!.Trim().ToLowerInvariant(),
                    IsActive: request.IsActive ?? true),
                cancellationToken);
            return Results.Ok(record);
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
        {
            // Unique violation — most likely (company_id, code) clash.
            return Results.BadRequest(new { message = $"Tax code '{request.Code}' already exists for this company." });
        }
    });

accounting.MapPut(
    "/tax-codes/{id:guid}",
    async (
        Guid id,
        TaxCodeUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ITaxCodeStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        var validation = ValidateTaxCodeInput(request);
        if (validation is not null)
        {
            return Results.BadRequest(new { message = validation });
        }

        try
        {
            var updated = await store.UpdateAsync(
                session.ActiveCompanyId,
                id,
                new TaxCodeUpsertInput(
                    Code: request.Code!.Trim(),
                    Name: request.Name!.Trim(),
                    RatePercent: request.RatePercent ?? 0m,
                    AppliesTo: request.AppliesTo!.Trim().ToLowerInvariant(),
                    IsActive: request.IsActive ?? true),
                cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
        {
            return Results.BadRequest(new { message = $"Tax code '{request.Code}' already exists for this company." });
        }
    });

accounting.MapPost(
    "/tax-codes/{id:guid}/activate",
    async (
        Guid id,
        BusinessSessionContextAccessor sessionAccessor,
        ITaxCodeStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, true, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

accounting.MapPost(
    "/tax-codes/{id:guid}/deactivate",
    async (
        Guid id,
        BusinessSessionContextAccessor sessionAccessor,
        ITaxCodeStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, false, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

static string? ValidateTaxCodeInput(TaxCodeUpsertHttpRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Code)) return "Code is required.";
    if (request.Code.Length > 32) return "Code must be 32 characters or fewer.";
    if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
    if (request.Name.Length > 120) return "Name must be 120 characters or fewer.";
    if (request.RatePercent is null || request.RatePercent < 0m) return "Rate must be 0 or greater.";
    if (request.RatePercent > 100m) return "Rate must be 100 or lower.";
    if (string.IsNullOrWhiteSpace(request.AppliesTo) || !TaxCodeAppliesTo.IsValid(request.AppliesTo.Trim().ToLowerInvariant()))
    {
        return "Applies to must be 'sales', 'purchase', or 'both'.";
    }
    return null;
}

// ===========================================================================
// Payment terms catalog (per-company)
//
// V1 surface: list / create / update / activate-toggle. Backs the
// Settings → Payment Terms page and the per-vendor Payment Term picker.
// ===========================================================================

accounting.MapGet(
    "/payment-terms",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IPaymentTermStore store,
        bool? includeInactive,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var rows = await store.ListAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
        return Results.Ok(rows);
    });

accounting.MapPost(
    "/payment-terms",
    async (
        PaymentTermUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IPaymentTermStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidatePaymentTermInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.CreateAsync(
                session.ActiveCompanyId,
                new PaymentTermUpsertInput(
                    Code: request.Code!.Trim(),
                    Name: request.Name!.Trim(),
                    NetDays: request.NetDays ?? 0,
                    IsActive: request.IsActive ?? true),
                cancellationToken);
            return Results.Ok(saved);
        }
        catch (PostgresException pgEx) when (pgEx.SqlState == "23505")
        {
            return Results.BadRequest(new { message = $"Payment term '{request.Code}' already exists for this company." });
        }
    });

accounting.MapPut(
    "/payment-terms/{id:guid}",
    async (
        Guid id,
        PaymentTermUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IPaymentTermStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidatePaymentTermInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.UpdateAsync(
                session.ActiveCompanyId,
                id,
                new PaymentTermUpsertInput(
                    Code: request.Code!.Trim(),
                    Name: request.Name!.Trim(),
                    NetDays: request.NetDays ?? 0,
                    IsActive: request.IsActive ?? true),
                cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (PostgresException pgEx) when (pgEx.SqlState == "23505")
        {
            return Results.BadRequest(new { message = $"Payment term '{request.Code}' already exists for this company." });
        }
    });

accounting.MapPost(
    "/payment-terms/{id:guid}/activate",
    async (
        Guid id,
        BusinessSessionContextAccessor sessionAccessor,
        IPaymentTermStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, true, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

accounting.MapPost(
    "/payment-terms/{id:guid}/deactivate",
    async (
        Guid id,
        BusinessSessionContextAccessor sessionAccessor,
        IPaymentTermStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, false, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

static string? ValidatePaymentTermInput(PaymentTermUpsertHttpRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Code)) return "Code is required.";
    if (request.Code.Length > 32) return "Code must be 32 characters or fewer.";
    if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
    if (request.Name.Length > 120) return "Name must be 120 characters or fewer.";
    if (request.NetDays is null || request.NetDays < 0) return "Net days must be 0 or greater.";
    if (request.NetDays > 3650) return "Net days must be 3650 or fewer.";
    return null;
}

// ===========================================================================
// Sales-side pre-billing documents: Quotes (estimates) + Sales Orders.
// No GL impact. Quote → Sales Order via convert-to-sales-order; Sales
// Order → Invoice is V1-decoupled (the SO records a free-text invoice
// number when the user marks it invoiced; actual invoice posting stays
// in the existing Invoice flow).
// ===========================================================================

accounting.MapGet(
    "/quotes",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IQuoteStore store,
        bool? includeDrafts,
        string? status,
        Guid? customerId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var filter = new QuoteListFilter(
            IncludeDrafts: includeDrafts ?? true,
            Status: string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant(),
            CustomerId: customerId,
            FromDate: from,
            ToDate: to);
        var rows = await store.ListAsync(session.ActiveCompanyId, filter, cancellationToken);
        return Results.Ok(rows);
    });

accounting.MapGet(
    "/quotes/{quoteId:guid}",
    async (
        Guid quoteId,
        BusinessSessionContextAccessor sessionAccessor,
        IQuoteStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var quote = await store.GetByIdAsync(session.ActiveCompanyId, quoteId, cancellationToken);
        return quote is null ? Results.NotFound() : Results.Ok(quote);
    });

accounting.MapPost(
    "/quotes",
    async (
        QuoteUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IQuoteStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateQuoteInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.CreateAsync(
                session.ActiveCompanyId,
                MapQuoteInput(request),
                cancellationToken);
            return Results.Ok(saved);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { message = "Could not allocate a unique quote number. Please try saving again." });
        }
    });

accounting.MapPut(
    "/quotes/{quoteId:guid}",
    async (
        Guid quoteId,
        QuoteUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IQuoteStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateQuoteInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.UpdateAsync(
                session.ActiveCompanyId,
                quoteId,
                MapQuoteInput(request),
                cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/quotes/{quoteId:guid}/status",
    async (
        Guid quoteId,
        QuoteStatusHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IQuoteStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var newStatus = request.Status?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(newStatus) || !QuoteStatus.IsValid(newStatus))
        {
            return Results.BadRequest(new { message = "Status is required and must be one of: draft, pending, accepted, rejected, expired, void." });
        }
        if (newStatus == QuoteStatus.Converted)
        {
            return Results.BadRequest(new { message = "Use POST /quotes/{id}/convert-to-sales-order to mark a quote as converted." });
        }

        try
        {
            var saved = await store.SetStatusAsync(session.ActiveCompanyId, quoteId, newStatus, cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/quotes/{quoteId:guid}/convert-to-sales-order",
    async (
        Guid quoteId,
        BusinessSessionContextAccessor sessionAccessor,
        IQuoteStore quotes,
        ISalesOrderStore salesOrders,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var quote = await quotes.GetByIdAsync(session.ActiveCompanyId, quoteId, cancellationToken);
        if (quote is null) return Results.NotFound();
        if (quote.Status == QuoteStatus.Converted)
        {
            return Results.BadRequest(new { message = "Quote has already been converted." });
        }
        if (quote.Status != QuoteStatus.Accepted)
        {
            return Results.BadRequest(new { message = "Only Accepted quotes can be converted to a Sales Order." });
        }

        var soInput = new SalesOrderUpsertInput(
            CustomerId: quote.CustomerId,
            DocumentDate: DateOnly.FromDateTime(DateTime.UtcNow),
            TransactionCurrencyCode: quote.TransactionCurrencyCode,
            FxRate: quote.FxRate,
            BillingAddressLine: quote.BillingAddressLine,
            BillingCity: quote.BillingCity,
            BillingProvinceState: quote.BillingProvinceState,
            BillingPostalCode: quote.BillingPostalCode,
            BillingCountry: quote.BillingCountry,
            ShippingAddressLine: quote.ShippingAddressLine,
            ShippingCity: quote.ShippingCity,
            ShippingProvinceState: quote.ShippingProvinceState,
            ShippingPostalCode: quote.ShippingPostalCode,
            ShippingCountry: quote.ShippingCountry,
            ShipVia: quote.ShipVia,
            ShippingDate: quote.ShippingDate,
            TrackingNo: quote.TrackingNo,
            TaxMode: quote.TaxMode,
            DiscountKind: quote.DiscountKind,
            DiscountValue: quote.DiscountValue,
            ShippingAmount: quote.ShippingAmount,
            ShippingTaxCodeId: quote.ShippingTaxCodeId,
            MemoToCustomer: quote.MemoToCustomer,
            InternalNote: quote.InternalNote,
            SourceQuoteId: quote.Id,
            Lines: quote.Lines
                .Select(l => new SalesOrderLineInput(
                    Sequence: l.Sequence,
                    ServiceDate: l.ServiceDate,
                    ItemId: l.ItemId,
                    Description: l.Description,
                    Quantity: l.Quantity,
                    UnitPrice: l.UnitPrice,
                    TaxCodeId: l.TaxCodeId,
                    AccountCode: l.AccountCode))
                .ToArray());

        var so = await salesOrders.CreateAsync(session.ActiveCompanyId, soInput, cancellationToken);
        await quotes.MarkConvertedAsync(session.ActiveCompanyId, quote.Id, so.Id, cancellationToken);

        return Results.Ok(so);
    });

accounting.MapGet(
    "/sales-orders",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        ISalesOrderStore store,
        string? status,
        Guid? customerId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var filter = new SalesOrderListFilter(
            Status: string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant(),
            CustomerId: customerId,
            FromDate: from,
            ToDate: to);
        var rows = await store.ListAsync(session.ActiveCompanyId, filter, cancellationToken);
        return Results.Ok(rows);
    });

accounting.MapGet(
    "/sales-orders/{salesOrderId:guid}",
    async (
        Guid salesOrderId,
        BusinessSessionContextAccessor sessionAccessor,
        ISalesOrderStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var so = await store.GetByIdAsync(session.ActiveCompanyId, salesOrderId, cancellationToken);
        return so is null ? Results.NotFound() : Results.Ok(so);
    });

accounting.MapPost(
    "/sales-orders",
    async (
        SalesOrderUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ISalesOrderStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateSalesOrderInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.CreateAsync(
                session.ActiveCompanyId,
                MapSalesOrderInput(request),
                cancellationToken);
            return Results.Ok(saved);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { message = "Could not allocate a unique sales order number. Please try saving again." });
        }
    });

accounting.MapPut(
    "/sales-orders/{salesOrderId:guid}",
    async (
        Guid salesOrderId,
        SalesOrderUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ISalesOrderStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateSalesOrderInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.UpdateAsync(
                session.ActiveCompanyId,
                salesOrderId,
                MapSalesOrderInput(request),
                cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/sales-orders/{salesOrderId:guid}/status",
    async (
        Guid salesOrderId,
        SalesOrderStatusHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ISalesOrderStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var newStatus = request.Status?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(newStatus) || !SalesOrderStatus.IsValid(newStatus))
        {
            return Results.BadRequest(new { message = "Status is required and must be one of: open, invoiced, cancelled." });
        }
        if (newStatus == SalesOrderStatus.Invoiced)
        {
            return Results.BadRequest(new { message = "Use POST /sales-orders/{id}/mark-invoiced with an invoice number." });
        }

        try
        {
            var saved = await store.SetStatusAsync(session.ActiveCompanyId, salesOrderId, newStatus, cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/sales-orders/{salesOrderId:guid}/mark-invoiced",
    async (
        Guid salesOrderId,
        SalesOrderInvoicedHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ISalesOrderStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.InvoiceNumber))
        {
            return Results.BadRequest(new { message = "Invoice number is required." });
        }
        if (request.InvoiceNumber.Length > 64)
        {
            return Results.BadRequest(new { message = "Invoice number must be 64 characters or fewer." });
        }

        var saved = await store.MarkInvoicedAsync(session.ActiveCompanyId, salesOrderId, request.InvoiceNumber, cancellationToken);
        return saved is null ? Results.NotFound() : Results.Ok(saved);
    });

static string? ValidateQuoteInput(QuoteUpsertHttpRequest request)
{
    if (request.CustomerId == Guid.Empty) return "Customer is required.";
    if (string.IsNullOrWhiteSpace(request.TransactionCurrencyCode)) return "Transaction currency is required.";
    if (request.TransactionCurrencyCode.Length != 3) return "Transaction currency must be a 3-letter code.";
    var taxMode = string.IsNullOrWhiteSpace(request.TaxMode) ? QuoteTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant();
    if (!QuoteTaxMode.IsValid(taxMode)) return "Tax mode must be 'exclusive' or 'inclusive'.";
    if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
    foreach (var line in request.Lines)
    {
        if (line.Quantity < 0) return "Line quantity must be 0 or greater.";
        if (line.UnitPrice < 0) return "Line unit price must be 0 or greater.";
    }
    return null;
}

// ---------------------------------------------------------------------------
// Maps an InvoiceTemplate domain record into the wire shape that the
// Settings UI consumes. Flat structure (no nested config object) so a
// JSON typed-deserialize on the client stays trivial.
// ---------------------------------------------------------------------------
static object MapInvoiceTemplate(InvoiceTemplate template) => new
{
    template.Id,
    template.CompanyId,
    template.Name,
    template.IsDefault,
    template.Config.LogoUrl,
    template.Config.PrimaryColorHex,
    template.Config.AccentColorHex,
    template.Config.Tagline,
    template.Config.Greeting,
    template.Config.PaymentInstructions,
    template.Config.FooterNote,
    template.Config.ShowTaxColumn,
    template.Config.EmailSubjectTemplate,
    template.Config.EmailBodyTemplate,
    template.CreatedAt,
    template.UpdatedAt,
};

// ---------------------------------------------------------------------------
// Validates and maps the wire-format upsert request into the Application-
// layer InvoiceTemplateConfig. Returns the parsed config plus a non-null
// error string when validation fails.
// ---------------------------------------------------------------------------
static (InvoiceTemplateConfig Config, string? Error) TryReadInvoiceTemplateConfig(
    InvoiceTemplateUpsertHttpRequest request)
{
    var defaults = InvoiceTemplateConfig.Default;

    var primary = string.IsNullOrWhiteSpace(request.PrimaryColorHex)
        ? defaults.PrimaryColorHex
        : request.PrimaryColorHex!.Trim();
    if (!IsValidHexColor(primary))
    {
        return (defaults, $"Primary color '{primary}' is not a valid hex color (expected #RRGGBB).");
    }

    var accent = string.IsNullOrWhiteSpace(request.AccentColorHex)
        ? defaults.AccentColorHex
        : request.AccentColorHex!.Trim();
    if (!IsValidHexColor(accent))
    {
        return (defaults, $"Accent color '{accent}' is not a valid hex color (expected #RRGGBB).");
    }

    var config = new InvoiceTemplateConfig(
        LogoUrl: TrimToNull(request.LogoUrl),
        PrimaryColorHex: primary,
        AccentColorHex: accent,
        Tagline: TrimToNull(request.Tagline),
        Greeting: request.Greeting?.Trim() ?? defaults.Greeting,
        PaymentInstructions: request.PaymentInstructions?.Trim() ?? string.Empty,
        FooterNote: request.FooterNote?.Trim() ?? defaults.FooterNote,
        ShowTaxColumn: request.ShowTaxColumn ?? defaults.ShowTaxColumn,
        EmailSubjectTemplate: string.IsNullOrWhiteSpace(request.EmailSubjectTemplate)
            ? defaults.EmailSubjectTemplate
            : request.EmailSubjectTemplate!.Trim(),
        EmailBodyTemplate: request.EmailBodyTemplate?.Trim() ?? string.Empty);

    return (config, null);
}

static bool IsValidHexColor(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return false;
    if (!value.StartsWith('#')) return false;
    if (value.Length is not (4 or 7 or 9)) return false;
    for (var i = 1; i < value.Length; i++)
    {
        var c = value[i];
        var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        if (!ok) return false;
    }
    return true;
}

static string? TrimToNull(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var trimmed = value.Trim();
    return trimmed.Length == 0 ? null : trimmed;
}

// ---------------------------------------------------------------------------
// Synthesizes a stand-in invoice projection for the template preview
// endpoint so the editor can render a real PDF before any actual invoice
// exists. Numbers / dates / line text are deliberately recognizable as
// sample data ("INV-PREVIEW", "Acme Co.") so an operator who downloads
// it doesn't mistake the preview for a real document.
// ---------------------------------------------------------------------------
static InvoiceReviewProjection BuildSampleInvoiceProjection(string currencyCode)
{
    var documentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
    return new InvoiceReviewProjection(
        DisplayNumber: "INV-PREVIEW",
        EntityNumber: "EN0000PREVIEW",
        DocumentDate: documentDate,
        DueDate: documentDate.AddDays(30),
        Status: "preview",
        CounterpartyDisplayName: "Acme Co.",
        TransactionCurrencyCode: string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode,
        SubtotalAmount: 175m,
        TaxAmount: 22.75m,
        TotalAmount: 197.75m,
        Memo: "Sample preview — replace with real invoice content when sending.",
        Lines:
        [
            new InvoiceReviewLineProjection(1, "Design retainer (sample)", 1m, 100m, 100m, 13m),
            new InvoiceReviewLineProjection(2, "Hosting (sample)", 3m, 25m, 75m, 9.75m),
        ]);
}

// ---------------------------------------------------------------------------
// Splits a comma / semicolon-separated email string ("a@x.com, b@x.com")
// into a trimmed list, dropping anything without '@'. Used by the invoice
// send endpoint so operators can paste recipient lists straight from a
// CRM or email-thread copy without us caring about delimiter style.
// ---------------------------------------------------------------------------
static IReadOnlyList<string> SplitEmailList(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return Array.Empty<string>();
    }

    return raw
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => s.Contains('@', StringComparison.Ordinal))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static QuoteUpsertInput MapQuoteInput(QuoteUpsertHttpRequest request) => new(
    CustomerId: request.CustomerId,
    DocumentDate: request.DocumentDate,
    ExpirationDate: request.ExpirationDate,
    TransactionCurrencyCode: request.TransactionCurrencyCode,
    FxRate: request.FxRate,
    BillingAddressLine: request.BillingAddressLine,
    BillingCity: request.BillingCity,
    BillingProvinceState: request.BillingProvinceState,
    BillingPostalCode: request.BillingPostalCode,
    BillingCountry: request.BillingCountry,
    ShippingAddressLine: request.ShippingAddressLine,
    ShippingCity: request.ShippingCity,
    ShippingProvinceState: request.ShippingProvinceState,
    ShippingPostalCode: request.ShippingPostalCode,
    ShippingCountry: request.ShippingCountry,
    ShipVia: request.ShipVia,
    ShippingDate: request.ShippingDate,
    TrackingNo: request.TrackingNo,
    TaxMode: string.IsNullOrWhiteSpace(request.TaxMode) ? QuoteTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant(),
    DiscountKind: request.DiscountKind,
    DiscountValue: request.DiscountValue,
    ShippingAmount: request.ShippingAmount,
    ShippingTaxCodeId: request.ShippingTaxCodeId,
    MemoToCustomer: request.MemoToCustomer,
    InternalNote: request.InternalNote,
    Lines: (request.Lines ?? Array.Empty<QuoteLineHttpRequest>())
        .Select(l => new QuoteLineInput(
            Sequence: l.Sequence,
            ServiceDate: l.ServiceDate,
            ItemId: l.ItemId,
            Description: l.Description ?? string.Empty,
            Quantity: l.Quantity,
            UnitPrice: l.UnitPrice,
            TaxCodeId: l.TaxCodeId,
            AccountCode: l.AccountCode))
        .ToArray());

static string? ValidateSalesOrderInput(SalesOrderUpsertHttpRequest request)
{
    if (request.CustomerId == Guid.Empty) return "Customer is required.";
    if (string.IsNullOrWhiteSpace(request.TransactionCurrencyCode)) return "Transaction currency is required.";
    if (request.TransactionCurrencyCode.Length != 3) return "Transaction currency must be a 3-letter code.";
    var taxMode = string.IsNullOrWhiteSpace(request.TaxMode) ? QuoteTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant();
    if (!QuoteTaxMode.IsValid(taxMode)) return "Tax mode must be 'exclusive' or 'inclusive'.";
    if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
    foreach (var line in request.Lines)
    {
        if (line.Quantity < 0) return "Line quantity must be 0 or greater.";
        if (line.UnitPrice < 0) return "Line unit price must be 0 or greater.";
    }
    return null;
}

static SalesOrderUpsertInput MapSalesOrderInput(SalesOrderUpsertHttpRequest request) => new(
    CustomerId: request.CustomerId,
    DocumentDate: request.DocumentDate,
    TransactionCurrencyCode: request.TransactionCurrencyCode,
    FxRate: request.FxRate,
    BillingAddressLine: request.BillingAddressLine,
    BillingCity: request.BillingCity,
    BillingProvinceState: request.BillingProvinceState,
    BillingPostalCode: request.BillingPostalCode,
    BillingCountry: request.BillingCountry,
    ShippingAddressLine: request.ShippingAddressLine,
    ShippingCity: request.ShippingCity,
    ShippingProvinceState: request.ShippingProvinceState,
    ShippingPostalCode: request.ShippingPostalCode,
    ShippingCountry: request.ShippingCountry,
    ShipVia: request.ShipVia,
    ShippingDate: request.ShippingDate,
    TrackingNo: request.TrackingNo,
    TaxMode: string.IsNullOrWhiteSpace(request.TaxMode) ? QuoteTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant(),
    DiscountKind: request.DiscountKind,
    DiscountValue: request.DiscountValue,
    ShippingAmount: request.ShippingAmount,
    ShippingTaxCodeId: request.ShippingTaxCodeId,
    MemoToCustomer: request.MemoToCustomer,
    InternalNote: request.InternalNote,
    SourceQuoteId: request.SourceQuoteId,
    Lines: (request.Lines ?? Array.Empty<SalesOrderLineHttpRequest>())
        .Select(l => new SalesOrderLineInput(
            Sequence: l.Sequence,
            ServiceDate: l.ServiceDate,
            ItemId: l.ItemId,
            Description: l.Description ?? string.Empty,
            Quantity: l.Quantity,
            UnitPrice: l.UnitPrice,
            TaxCodeId: l.TaxCodeId,
            AccountCode: l.AccountCode))
        .ToArray());

// ===========================================================================
// Bills (vendor invoices) — AP-side document lifecycle.
//
// V1 surface: list / get / create-as-draft / edit-draft / post / void.
// Post is a pure status transition in V1; the heavy posting pipeline
// (PostBillCommandHandler — FX snapshot, AP open item, journal entry)
// gets wired in alongside the PO + Inventory batch. Void is also pure
// status today; reversing the GL entries lands with the same batch.
// ===========================================================================

accounting.MapGet(
    "/ap/bills",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IBillStore store,
        bool? includeDrafts,
        string? status,
        Guid? vendorId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var filter = new BillListFilter(
            IncludeDrafts: includeDrafts ?? true,
            Status: string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant(),
            VendorId: vendorId,
            FromDate: from,
            ToDate: to);
        var rows = await store.ListAsync(session.ActiveCompanyId, filter, cancellationToken);
        return Results.Ok(rows);
    });

accounting.MapGet(
    "/ap/bills/{billId:guid}",
    async (
        Guid billId,
        BusinessSessionContextAccessor sessionAccessor,
        IBillStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var bill = await store.GetByIdAsync(session.ActiveCompanyId, billId, cancellationToken);
        return bill is null ? Results.NotFound() : Results.Ok(bill);
    });

accounting.MapPost(
    "/ap/bills",
    async (
        BillUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IBillStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        if (session.UserId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateBillInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.CreateAsync(
                session.ActiveCompanyId,
                session.UserId,
                MapBillInput(request),
                cancellationToken);
            return Results.Ok(saved);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.BadRequest(new { message = $"Bill number '{request.BillNumber}' already exists for this vendor / company." });
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            return Results.BadRequest(new { message = "A referenced row (vendor / currency / payment term) was not found." });
        }
    });

accounting.MapPut(
    "/ap/bills/{billId:guid}",
    async (
        Guid billId,
        BillUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IBillStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateBillInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.UpdateAsync(
                session.ActiveCompanyId,
                billId,
                MapBillInput(request),
                cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.BadRequest(new { message = $"Bill number '{request.BillNumber}' already exists for this vendor / company." });
        }
    });

accounting.MapPost(
    "/ap/bills/{billId:guid}/post",
    async (
        Guid billId,
        BusinessSessionContextAccessor sessionAccessor,
        IBillStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        try
        {
            var saved = await store.PostAsync(session.ActiveCompanyId, billId, cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/ap/bills/{billId:guid}/void",
    async (
        Guid billId,
        BusinessSessionContextAccessor sessionAccessor,
        IBillStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        try
        {
            var saved = await store.VoidAsync(session.ActiveCompanyId, billId, cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

static string? ValidateBillInput(BillUpsertHttpRequest request)
{
    if (string.IsNullOrWhiteSpace(request.BillNumber)) return "Bill number is required (use the supplier's invoice number).";
    if (request.BillNumber.Length > 64) return "Bill number must be 64 characters or fewer.";
    if (request.VendorId == Guid.Empty) return "Vendor is required.";
    if (string.IsNullOrWhiteSpace(request.DocumentCurrencyCode)) return "Document currency is required.";
    if (request.DocumentCurrencyCode.Length != 3) return "Document currency must be a 3-letter code.";
    if (request.DueDate < request.BillDate) return "Due date cannot be before bill date.";
    if (request.FxRate is { } rate && rate <= 0m) return "Exchange rate must be greater than zero.";
    if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
    foreach (var line in request.Lines)
    {
        if (line.ExpenseAccountId == Guid.Empty) return "Each line must point to a category account.";
        if (line.LineAmount < 0m) return "Line amount must be 0 or greater.";
        if (line.TaxAmount is { } tax && tax < 0m) return "Tax amount must be 0 or greater.";
    }
    return null;
}

static BillUpsertInput MapBillInput(BillUpsertHttpRequest request) =>
    new(
        BillNumber: request.BillNumber,
        VendorId: request.VendorId,
        BillDate: request.BillDate,
        DueDate: request.DueDate,
        DocumentCurrencyCode: request.DocumentCurrencyCode,
        FxRate: request.FxRate,
        Memo: request.Memo,
        PaymentTermId: request.PaymentTermId,
        SourcePurchaseOrderId: request.SourcePurchaseOrderId,
        SourcePurchaseOrderNumber: request.SourcePurchaseOrderNumber,
        Lines: (request.Lines ?? Array.Empty<BillLineHttpRequest>())
            .Select(l => new BillLineInput(
                LineNumber: l.LineNumber,
                ExpenseAccountId: l.ExpenseAccountId,
                Description: l.Description ?? string.Empty,
                LineAmount: l.LineAmount,
                TaxCodeId: l.TaxCodeId,
                TaxAmount: l.TaxAmount ?? 0m))
            .ToArray());

// ===========================================================================
// Purchase Orders (AP-side, /ap/purchase-orders) — pre-bill commitments.
//
// Uses the brand-neutral Modules.AP.PurchaseOrders module backed by the
// ap_purchase_orders / ap_purchase_order_lines tables. Distinct from
// the inventory-grade purchase_orders table that the existing posting
// infrastructure owns; convergence is an Inventory-batch migration.
//
// Convert to Bill: atomic — creates a Bill (Draft) populated from the
// PO's lines, marks the PO as Closed with cross-references on both
// sides. Convert to Expense lands when the Expense module ships.
// ===========================================================================

accounting.MapGet(
    "/ap/purchase-orders",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IPurchaseOrderStore store,
        bool? includeDrafts,
        string? status,
        Guid? vendorId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var filter = new PurchaseOrderListFilter(
            IncludeDrafts: includeDrafts ?? true,
            Status: string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant(),
            VendorId: vendorId,
            FromDate: from,
            ToDate: to);
        var rows = await store.ListAsync(session.ActiveCompanyId, filter, cancellationToken);
        return Results.Ok(rows);
    });

accounting.MapGet(
    "/ap/purchase-orders/{purchaseOrderId:guid}",
    async (
        Guid purchaseOrderId,
        BusinessSessionContextAccessor sessionAccessor,
        IPurchaseOrderStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var po = await store.GetByIdAsync(session.ActiveCompanyId, purchaseOrderId, cancellationToken);
        return po is null ? Results.NotFound() : Results.Ok(po);
    });

accounting.MapPost(
    "/ap/purchase-orders",
    async (
        PurchaseOrderUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IPurchaseOrderStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidatePurchaseOrderInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.CreateAsync(
                session.ActiveCompanyId,
                MapPurchaseOrderInput(request),
                cancellationToken);
            return Results.Ok(saved);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { message = "Could not allocate a unique purchase-order number. Please try saving again." });
        }
    });

accounting.MapPut(
    "/ap/purchase-orders/{purchaseOrderId:guid}",
    async (
        Guid purchaseOrderId,
        PurchaseOrderUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IPurchaseOrderStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidatePurchaseOrderInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.UpdateAsync(
                session.ActiveCompanyId,
                purchaseOrderId,
                MapPurchaseOrderInput(request),
                cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/ap/purchase-orders/{purchaseOrderId:guid}/status",
    async (
        Guid purchaseOrderId,
        PurchaseOrderStatusHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IPurchaseOrderStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var newStatus = request.Status?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(newStatus) || !PurchaseOrderStatus.IsValid(newStatus))
        {
            return Results.BadRequest(new { message = "Status is required and must be one of: draft, open, closed, cancelled, void." });
        }

        try
        {
            var saved = await store.SetStatusAsync(session.ActiveCompanyId, purchaseOrderId, newStatus, cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/ap/purchase-orders/{purchaseOrderId:guid}/convert-to-bill",
    async (
        Guid purchaseOrderId,
        BusinessSessionContextAccessor sessionAccessor,
        IPurchaseOrderStore poStore,
        IBillStore billStore,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        if (session.UserId == Guid.Empty) return Results.Unauthorized();

        var po = await poStore.GetByIdAsync(session.ActiveCompanyId, purchaseOrderId, cancellationToken);
        if (po is null) return Results.NotFound();
        if (!PurchaseOrderStatus.CanConvert(po.Status))
        {
            return Results.BadRequest(new { message = $"Purchase order in status '{po.Status}' cannot be converted to a Bill. Open or Closed POs are eligible." });
        }
        if (po.Lines.Count == 0)
        {
            return Results.BadRequest(new { message = "Purchase order has no lines to convert." });
        }
        if (po.Lines.Any(l => l.ExpenseAccountId is null))
        {
            return Results.BadRequest(new { message = "All purchase-order lines must have a category before converting to Bill (V1 supports Category-mode lines only)." });
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var billInput = new BillUpsertInput(
            BillNumber: $"PENDING-{po.PurchaseOrderNumber}",
            VendorId: po.VendorId,
            BillDate: today,
            DueDate: today,
            DocumentCurrencyCode: po.TransactionCurrencyCode,
            FxRate: po.FxRate,
            Memo: po.MemoToSupplier,
            PaymentTermId: po.PaymentTermId,
            SourcePurchaseOrderId: po.Id,
            SourcePurchaseOrderNumber: po.PurchaseOrderNumber,
            Lines: po.Lines
                .Select((l, i) => new BillLineInput(
                    LineNumber: i + 1,
                    ExpenseAccountId: l.ExpenseAccountId!.Value,
                    Description: l.Description,
                    LineAmount: Math.Round(l.Quantity * l.UnitPrice, 6),
                    TaxCodeId: l.TaxCodeId,
                    TaxAmount: 0m))
                .ToArray());

        BillRecord savedBill;
        try
        {
            savedBill = await billStore.CreateAsync(
                session.ActiveCompanyId,
                session.UserId,
                billInput,
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.BadRequest(new { message = $"A bill with number 'PENDING-{po.PurchaseOrderNumber}' already exists. Edit it directly or update the bill number to convert again." });
        }

        await poStore.MarkClosedAsync(session.ActiveCompanyId, po.Id, cancellationToken);

        return Results.Ok(savedBill);
    });

static string? ValidatePurchaseOrderInput(PurchaseOrderUpsertHttpRequest request)
{
    if (request.VendorId == Guid.Empty) return "Vendor is required.";
    if (string.IsNullOrWhiteSpace(request.TransactionCurrencyCode)) return "Transaction currency is required.";
    if (request.TransactionCurrencyCode.Length != 3) return "Transaction currency must be a 3-letter code.";
    var taxMode = string.IsNullOrWhiteSpace(request.TaxMode) ? PurchaseOrderTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant();
    if (!PurchaseOrderTaxMode.IsValid(taxMode)) return "Tax mode must be 'exclusive' or 'inclusive'.";
    if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
    foreach (var line in request.Lines)
    {
        if (line.ExpenseAccountId is null && line.ItemId is null)
            return "Each line must have a category (Item-mode lines land with the Inventory batch).";
        if (line.Quantity < 0) return "Line quantity must be 0 or greater.";
        if (line.UnitPrice < 0) return "Line unit price must be 0 or greater.";
    }
    return null;
}

static PurchaseOrderUpsertInput MapPurchaseOrderInput(PurchaseOrderUpsertHttpRequest request) => new(
    VendorId: request.VendorId,
    OrderDate: request.OrderDate,
    ExpectedDeliveryDate: request.ExpectedDeliveryDate,
    TransactionCurrencyCode: request.TransactionCurrencyCode,
    FxRate: request.FxRate,
    BillingAddressLine: request.BillingAddressLine,
    BillingCity: request.BillingCity,
    BillingProvinceState: request.BillingProvinceState,
    BillingPostalCode: request.BillingPostalCode,
    BillingCountry: request.BillingCountry,
    ShippingAddressLine: request.ShippingAddressLine,
    ShippingCity: request.ShippingCity,
    ShippingProvinceState: request.ShippingProvinceState,
    ShippingPostalCode: request.ShippingPostalCode,
    ShippingCountry: request.ShippingCountry,
    ShipVia: request.ShipVia,
    ShippingDate: request.ShippingDate,
    TrackingNo: request.TrackingNo,
    TaxMode: string.IsNullOrWhiteSpace(request.TaxMode) ? PurchaseOrderTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant(),
    DiscountKind: request.DiscountKind,
    DiscountValue: request.DiscountValue,
    ShippingAmount: request.ShippingAmount,
    ShippingTaxCodeId: request.ShippingTaxCodeId,
    MemoToSupplier: request.MemoToSupplier,
    InternalNote: request.InternalNote,
    PaymentTermId: request.PaymentTermId,
    Lines: (request.Lines ?? Array.Empty<PurchaseOrderLineHttpRequest>())
        .Select(l => new PurchaseOrderLineInput(
            Sequence: l.Sequence,
            ServiceDate: l.ServiceDate,
            ItemId: l.ItemId,
            ExpenseAccountId: l.ExpenseAccountId,
            Description: l.Description ?? string.Empty,
            Quantity: l.Quantity,
            UnitPrice: l.UnitPrice,
            TaxCodeId: l.TaxCodeId))
        .ToArray());

// ===========================================================================
// Expenses (AP-side, /ap/expenses) — cash outflows.
//
// Posted-only state machine: an Expense reflects a payment that has
// already happened, so it lands directly in Posted state and only
// transitions out via Void. V1 framework writes the document but
// defers the journal-entry pipeline (DR category accounts / CR
// payment account) — same scheduling as Bill posting integration.
//
// Convert to Expense (from a PO): see /ap/purchase-orders/{id}/convert-to-expense
// below.
// ===========================================================================

accounting.MapGet(
    "/ap/expenses",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IExpenseStore store,
        string? status,
        Guid? payeeId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var filter = new ExpenseListFilter(
            Status: string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant(),
            PayeeId: payeeId,
            FromDate: from,
            ToDate: to);
        var rows = await store.ListAsync(session.ActiveCompanyId, filter, cancellationToken);
        return Results.Ok(rows);
    });

accounting.MapGet(
    "/ap/expenses/{expenseId:guid}",
    async (
        Guid expenseId,
        BusinessSessionContextAccessor sessionAccessor,
        IExpenseStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var expense = await store.GetByIdAsync(session.ActiveCompanyId, expenseId, cancellationToken);
        return expense is null ? Results.NotFound() : Results.Ok(expense);
    });

accounting.MapPost(
    "/ap/expenses",
    async (
        ExpenseUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IExpenseStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        if (session.UserId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateExpenseInput(request);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        try
        {
            var saved = await store.CreateAsync(
                session.ActiveCompanyId,
                session.UserId,
                MapExpenseInput(request),
                cancellationToken);
            return Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { message = "Could not allocate a unique expense number. Please try saving again." });
        }
    });

accounting.MapPost(
    "/ap/expenses/{expenseId:guid}/void",
    async (
        Guid expenseId,
        BusinessSessionContextAccessor sessionAccessor,
        IExpenseStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        try
        {
            var saved = await store.VoidAsync(session.ActiveCompanyId, expenseId, cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/ap/purchase-orders/{purchaseOrderId:guid}/convert-to-expense",
    async (
        Guid purchaseOrderId,
        ExpenseUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IPurchaseOrderStore poStore,
        IExpenseStore expenseStore,
        CancellationToken cancellationToken) =>
    {
        // PO → Expense conversion needs payment account / method / cheque
        // or ref number from the user — those don't exist on the PO. The
        // Blazor side opens a small dialog, collects them, and posts here
        // alongside the PO id.
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        if (session.UserId == Guid.Empty) return Results.Unauthorized();

        var po = await poStore.GetByIdAsync(session.ActiveCompanyId, purchaseOrderId, cancellationToken);
        if (po is null) return Results.NotFound();
        if (!PurchaseOrderStatus.CanConvert(po.Status))
        {
            return Results.BadRequest(new { message = $"Purchase order in status '{po.Status}' cannot be converted to an Expense." });
        }
        if (po.Lines.Count == 0)
        {
            return Results.BadRequest(new { message = "Purchase order has no lines to convert." });
        }
        if (po.Lines.Any(l => l.ExpenseAccountId is null))
        {
            return Results.BadRequest(new { message = "All purchase-order lines must have a category before converting to Expense." });
        }

        // Build a synthesised ExpenseUpsertHttpRequest from PO lines + the
        // payment/method bits the caller supplied.
        var expenseRequest = request with
        {
            // Payee defaults to vendor of the PO when caller didn't override.
            PayeeKind = string.IsNullOrWhiteSpace(request.PayeeKind) ? ExpensePayeeKind.Vendor : request.PayeeKind,
            PayeeId = request.PayeeId ?? po.VendorId,
            PayeeNameFreeform = string.IsNullOrWhiteSpace(request.PayeeNameFreeform) ? po.VendorName : request.PayeeNameFreeform,
            TransactionCurrencyCode = string.IsNullOrWhiteSpace(request.TransactionCurrencyCode) ? po.TransactionCurrencyCode : request.TransactionCurrencyCode,
            FxRate = request.FxRate ?? po.FxRate,
            SourcePurchaseOrderId = po.Id,
            SourcePurchaseOrderNumber = po.PurchaseOrderNumber,
            Memo = request.Memo ?? po.MemoToSupplier,
            Lines = po.Lines
                .Select(l => new ExpenseLineHttpRequest(
                    Sequence: l.Sequence,
                    ServiceDate: l.ServiceDate,
                    ItemId: null,
                    ExpenseAccountId: l.ExpenseAccountId!.Value,
                    Description: l.Description,
                    Quantity: l.Quantity,
                    UnitPrice: l.UnitPrice,
                    TaxCodeId: l.TaxCodeId))
                .ToArray()
        };

        var validation = ValidateExpenseInput(expenseRequest);
        if (validation is not null) return Results.BadRequest(new { message = validation });

        ExpenseRecord savedExpense;
        try
        {
            savedExpense = await expenseStore.CreateAsync(
                session.ActiveCompanyId,
                session.UserId,
                MapExpenseInput(expenseRequest),
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }

        await poStore.MarkClosedAsync(session.ActiveCompanyId, po.Id, cancellationToken);

        return Results.Ok(savedExpense);
    });

static string? ValidateExpenseInput(ExpenseUpsertHttpRequest request)
{
    if (!ExpensePayeeKind.IsValid(request.PayeeKind))
        return "Payee kind must be 'vendor', 'employee', or 'other'.";
    if (request.PayeeId is null && string.IsNullOrWhiteSpace(request.PayeeNameFreeform))
        return "Payee is required (pick a vendor / employee or enter a free-form name).";
    if (request.PaymentAccountId == Guid.Empty)
        return "Payment account is required.";
    if (!ExpensePaymentMethod.IsValid(request.PaymentMethod))
        return "Invalid payment method.";

    var refValidation = ExpensePaymentMethod.ValidateReferenceFields(request.PaymentMethod, request.ChequeNumber, request.RefNo);
    if (refValidation is not null) return refValidation;

    if (string.IsNullOrWhiteSpace(request.TransactionCurrencyCode)) return "Transaction currency is required.";
    if (request.TransactionCurrencyCode.Length != 3) return "Transaction currency must be a 3-letter code.";
    if (request.FxRate is { } rate && rate <= 0m) return "Exchange rate must be greater than zero.";

    var taxMode = string.IsNullOrWhiteSpace(request.TaxMode) ? ExpenseTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant();
    if (!ExpenseTaxMode.IsValid(taxMode)) return "Tax mode must be 'exclusive' or 'inclusive'.";

    if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
    foreach (var line in request.Lines)
    {
        if (line.ExpenseAccountId == Guid.Empty) return "Each line must point to a category account.";
        if (line.Quantity < 0) return "Line quantity must be 0 or greater.";
        if (line.UnitPrice < 0) return "Line unit price must be 0 or greater.";
    }
    return null;
}

static ExpenseUpsertInput MapExpenseInput(ExpenseUpsertHttpRequest request) => new(
    PayeeKind: request.PayeeKind,
    PayeeId: request.PayeeId,
    PayeeNameFreeform: request.PayeeNameFreeform ?? string.Empty,
    PaymentAccountId: request.PaymentAccountId,
    PaymentMethod: request.PaymentMethod,
    ChequeNumber: string.IsNullOrWhiteSpace(request.ChequeNumber) ? null : request.ChequeNumber.Trim(),
    RefNo: string.IsNullOrWhiteSpace(request.RefNo) ? null : request.RefNo.Trim(),
    TransactionCurrencyCode: request.TransactionCurrencyCode,
    FxRate: request.FxRate,
    PaymentDate: request.PaymentDate,
    SourcePurchaseOrderId: request.SourcePurchaseOrderId,
    SourcePurchaseOrderNumber: request.SourcePurchaseOrderNumber,
    TaxMode: string.IsNullOrWhiteSpace(request.TaxMode) ? ExpenseTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant(),
    DiscountKind: request.DiscountKind,
    DiscountValue: request.DiscountValue,
    Memo: request.Memo,
    InternalNote: request.InternalNote,
    Lines: (request.Lines ?? Array.Empty<ExpenseLineHttpRequest>())
        .Select(l => new ExpenseLineInput(
            Sequence: l.Sequence,
            ServiceDate: l.ServiceDate,
            ItemId: l.ItemId,
            ExpenseAccountId: l.ExpenseAccountId,
            Description: l.Description ?? string.Empty,
            Quantity: l.Quantity,
            UnitPrice: l.UnitPrice,
            TaxCodeId: l.TaxCodeId))
        .ToArray());

// ===========================================================================
// Chart of Accounts (per-company)
//
// V1 surface: list / create / update / activate-toggle. The UnitySearch
// projection (PostgreSqlUnitySearchProjectionStore.SeedAccountDocumentsAsync)
// already reads the same table on its periodic refresh, so newly-created
// accounts appear in the journal-entry account picker automatically.
// is_system rows are protected — UI-issued updates / deactivations refuse
// to modify them so AR / AP / FX control accounts stay stable.
// ===========================================================================

accounting.MapGet(
    "/accounts",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IAccountStore store,
        bool? includeInactive,
        string? rootType,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var rows = await store.ListAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
        if (!string.IsNullOrWhiteSpace(rootType))
        {
            var wanted = rootType.Trim().ToLowerInvariant();
            if (AccountRootType.IsValid(wanted))
            {
                rows = rows.Where(r => string.Equals(r.RootType, wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
        }
        return Results.Ok(rows);
    });

accounting.MapPost(
    "/accounts",
    async (
        AccountUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IAccountStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateAccountInput(request);
        if (validation is not null)
        {
            return Results.BadRequest(new { message = validation });
        }

        // Default the account currency to the company's base currency when
        // the user leaves the field blank. The Blazor form's "Defaults to
        // USD" placeholder describes this behaviour; without the API-side
        // default it would have stored NULL, which downstream FX / Trial
        // Balance code handles less cleanly than an explicit code.
        var requestedCurrency = string.IsNullOrWhiteSpace(request.CurrencyCode)
            ? sessionAccessor.CurrentResolution?.ActiveCompany.BaseCurrencyCode
            : request.CurrencyCode.Trim().ToUpperInvariant();

        try
        {
            var record = await store.CreateAsync(
                session.ActiveCompanyId,
                new AccountUpsertInput(
                    Code: request.Code!.Trim(),
                    Name: request.Name!.Trim(),
                    RootType: request.RootType!.Trim().ToLowerInvariant(),
                    DetailType: request.DetailType?.Trim(),
                    CurrencyCode: requestedCurrency,
                    AllowManualPosting: request.AllowManualPosting ?? true,
                    IsActive: request.IsActive ?? true),
                cancellationToken);
            return Results.Ok(record);
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
        {
            return Results.BadRequest(new { message = $"Account code '{request.Code}' already exists for this company." });
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23503")
        {
            // 23503 = foreign-key violation. The accounts table has multiple
            // FKs (company_id -> companies, currency_code -> currency_catalog)
            // — surface the specific constraint name so the operator can act
            // on it instead of being misled by a generic "Currency" message.
            var constraint = pgEx.ConstraintName ?? "(unknown)";
            var hint = constraint.Contains("currency", StringComparison.OrdinalIgnoreCase)
                ? $"Currency '{requestedCurrency}' is not in the platform currency catalog."
                : constraint.Contains("company", StringComparison.OrdinalIgnoreCase)
                    ? $"Active company '{session.ActiveCompanyId:D}' is not present in the persisted companies table — provision the company through the SysAdmin First-Company Wizard or enable bootstrap fixtures in this environment."
                    : $"A foreign-key reference is missing for this account row.";
            return Results.BadRequest(new
            {
                message = hint,
                constraint = constraint
            });
        }
    });

accounting.MapPut(
    "/accounts/{id:guid}",
    async (
        Guid id,
        AccountUpsertHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IAccountStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var validation = ValidateAccountInput(request);
        if (validation is not null)
        {
            return Results.BadRequest(new { message = validation });
        }

        // Same blank-currency default as the POST path, for consistency
        // when an operator clears the field on edit.
        var requestedCurrency = string.IsNullOrWhiteSpace(request.CurrencyCode)
            ? sessionAccessor.CurrentResolution?.ActiveCompany.BaseCurrencyCode
            : request.CurrencyCode.Trim().ToUpperInvariant();

        try
        {
            var updated = await store.UpdateAsync(
                session.ActiveCompanyId,
                id,
                new AccountUpsertInput(
                    Code: request.Code!.Trim(),
                    Name: request.Name!.Trim(),
                    RootType: request.RootType!.Trim().ToLowerInvariant(),
                    DetailType: request.DetailType?.Trim(),
                    CurrencyCode: requestedCurrency,
                    AllowManualPosting: request.AllowManualPosting ?? true,
                    IsActive: request.IsActive ?? true),
                cancellationToken);
            // Update returns null when the row is missing OR when is_system
            // blocked the WHERE clause. The maintenance UI hides edit on
            // system rows so a 404 here is the right honest response.
            return updated is null
                ? Results.NotFound(new { message = "Account not found, or it is a system control account that cannot be edited from this surface." })
                : Results.Ok(updated);
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
        {
            return Results.BadRequest(new { message = $"Account code '{request.Code}' already exists for this company." });
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23503")
        {
            var constraint = pgEx.ConstraintName ?? "(unknown)";
            var hint = constraint.Contains("currency", StringComparison.OrdinalIgnoreCase)
                ? $"Currency '{requestedCurrency}' is not in the platform currency catalog."
                : constraint.Contains("company", StringComparison.OrdinalIgnoreCase)
                    ? $"Active company '{session.ActiveCompanyId:D}' is not present in the persisted companies table — provision the company through the SysAdmin First-Company Wizard or enable bootstrap fixtures in this environment."
                    : $"A foreign-key reference is missing for this account row.";
            return Results.BadRequest(new
            {
                message = hint,
                constraint = constraint
            });
        }
    });

accounting.MapPost(
    "/accounts/{id:guid}/activate",
    async (
        Guid id,
        BusinessSessionContextAccessor sessionAccessor,
        IAccountStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, true, cancellationToken);
        return updated is null
            ? Results.NotFound(new { message = "Account not found or system-protected." })
            : Results.Ok(updated);
    });

accounting.MapPost(
    "/accounts/{id:guid}/deactivate",
    async (
        Guid id,
        BusinessSessionContextAccessor sessionAccessor,
        IAccountStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, false, cancellationToken);
        return updated is null
            ? Results.NotFound(new { message = "Account not found or system-protected." })
            : Results.Ok(updated);
    });

static string? ValidateAccountInput(AccountUpsertHttpRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Code)) return "Code is required.";
    if (request.Code.Length > 32) return "Code must be 32 characters or fewer.";
    if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
    if (request.Name.Length > 200) return "Name must be 200 characters or fewer.";
    if (string.IsNullOrWhiteSpace(request.RootType) || !AccountRootType.IsValid(request.RootType.Trim().ToLowerInvariant()))
    {
        return "Root type must be one of: asset, liability, equity, revenue, cost_of_sales, expense.";
    }
    if (request.DetailType is { Length: > 80 }) return "Detail type must be 80 characters or fewer.";
    if (!string.IsNullOrWhiteSpace(request.CurrencyCode))
    {
        var c = request.CurrencyCode.Trim();
        if (c.Length != 3) return "Currency code must be exactly 3 letters (ISO 4217).";
    }
    return null;
}

// ===========================================================================
// CoA starter templates
//
// V1 surface: list available templates + apply one to the active company.
// Application is additive (existing codes are skipped, never overwritten),
// so callers can safely retry. Templates are static C# data — the
// "version" field exposed here lets the audit trail tag which content
// was applied without a DB lookup.
// ===========================================================================

accounting.MapGet(
    "/accounts/templates",
    (
        BusinessSessionContextAccessor sessionAccessor,
        ICoaTemplateRegistry registry) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        return Results.Ok(registry.List().Select(t => new
        {
            key = t.Key,
            version = t.Version,
            name = t.Name,
            description = t.Description,
            country = t.Country,
            accountCodeLength = t.AccountCodeLength,
            accountCount = t.Accounts.Count,
            accounts = t.Accounts.Select(a => new
            {
                code = a.Code,
                name = a.Name,
                rootType = a.RootType,
                detailType = a.DetailType,
                allowManualPosting = a.AllowManualPosting,
                systemKey = a.SystemKey,
                systemRole = a.SystemRole,
            }).ToArray(),
        }).ToArray());
    });

accounting.MapPost(
    "/accounts/templates/{key}/apply",
    async (
        string key,
        BusinessSessionContextAccessor sessionAccessor,
        ICoaTemplateSeeder seeder,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        try
        {
            var summary = await seeder.SeedAsync(session.ActiveCompanyId, key, cancellationToken);
            return Results.Ok(summary);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Chart of accounts already seeded — re-applying is forbidden.
            return Results.Conflict(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/unitysearch/usage",
    async (
        UnitysearchUsageHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IUnitysearchEventStore eventStore,
        IUnitysearchUsageStatStore usageStore,
        IUnitysearchPairStatStore pairStore,
        IUnitysearchRecentQueryStore recentQueries,
        UnityAiFeatureFlagAccessor flags,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        var logger = loggerFactory.CreateLogger("unitysearch.usage");

        if (session is null || session.ActiveCompanyId == Guid.Empty)
        {
            return Results.Unauthorized();
        }
        if (request.CompanyId != Guid.Empty && request.CompanyId != session.ActiveCompanyId)
        {
            return Results.BadRequest(new { message = "company_id mismatch" });
        }
        if (string.IsNullOrWhiteSpace(request.Context) || string.IsNullOrWhiteSpace(request.EntityType) || string.IsNullOrWhiteSpace(request.EventType))
        {
            return Results.BadRequest(new { message = "context, entity_type, and event_type are required" });
        }

        if (!flags.UnitysearchLearningEnabled)
        {
            return Results.Ok(new { ok = true, learning = "disabled" });
        }

        var companyId = session.ActiveCompanyId;
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var now = DateTimeOffset.UtcNow;
        var normalizedQuery = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim().ToLowerInvariant();

        try
        {
            await eventStore.RecordEventAsync(new UnitysearchEventInput(
                CompanyId: companyId,
                UserId: userId,
                SessionId: request.SessionId,
                Context: request.Context.Trim(),
                EntityType: request.EntityType.Trim(),
                Query: request.Query,
                NormalizedQuery: normalizedQuery,
                EventType: request.EventType.Trim(),
                SelectedEntityId: request.SelectedEntityId,
                RankPosition: request.RankPosition,
                ResultCount: request.ResultCount,
                SourceRoute: request.SourceRoute,
                AnchorContext: request.AnchorContext,
                AnchorEntityType: request.AnchorEntityType,
                AnchorEntityId: request.AnchorEntityId,
                MetadataJson: request.MetadataJson), cancellationToken);

            if (string.Equals(request.EventType, UnitysearchEventType.Select, StringComparison.OrdinalIgnoreCase) && request.SelectedEntityId.HasValue)
            {
                await usageStore.UpsertOnSelectAsync(
                    companyId, userId, request.Context, request.EntityType, request.SelectedEntityId.Value,
                    request.RankPosition, request.Query, now, cancellationToken);

                if (!string.IsNullOrWhiteSpace(request.AnchorContext) &&
                    !string.IsNullOrWhiteSpace(request.AnchorEntityType) &&
                    request.AnchorEntityId.HasValue)
                {
                    await pairStore.UpsertOnSelectAsync(
                        companyId, userId,
                        request.AnchorContext!, request.AnchorEntityType!, request.AnchorEntityId.Value,
                        request.Context, request.EntityType, request.SelectedEntityId.Value,
                        now, cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(request.Query) && !string.IsNullOrWhiteSpace(normalizedQuery))
                {
                    await recentQueries.RecordAsync(
                        companyId, userId, request.Context, request.Query, normalizedQuery,
                        resultClicked: true,
                        clickedEntityType: request.EntityType,
                        clickedEntityId: request.SelectedEntityId,
                        resultCount: request.ResultCount,
                        createdAt: now,
                        cancellationToken);
                }
            }

            return Results.Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "unitysearch usage tracking failed (context={Context})", request.Context);
            return Results.Ok(new { ok = false, error = "tracking_failed" });
        }
    });

accounting.MapPost(
    "/reports/usage",
    async (
        ReportUsageHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IReportUsageEventStore eventStore,
        IReportUsageStatStore statStore,
        UnityAiFeatureFlagAccessor flags,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        var logger = loggerFactory.CreateLogger("reports.usage");

        if (session is null || session.ActiveCompanyId == Guid.Empty)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.ReportKey) || string.IsNullOrWhiteSpace(request.EventType))
        {
            return Results.BadRequest(new { message = "report_key and event_type are required" });
        }

        if (!flags.ReportUsageLearningEnabled)
        {
            return Results.Ok(new { ok = true, learning = "disabled" });
        }

        var companyId = session.ActiveCompanyId;
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var input = new ReportUsageEventInput(
            CompanyId: companyId,
            UserId: userId,
            ReportKey: request.ReportKey.Trim(),
            EventType: request.EventType.Trim(),
            DateRangeKey: request.DateRangeKey,
            FiltersJson: request.FiltersJson,
            SourceRoute: request.SourceRoute,
            MetadataJson: request.MetadataJson);

        try
        {
            await eventStore.RecordAsync(input, cancellationToken);
            await statStore.UpsertAsync(input, DateTimeOffset.UtcNow, cancellationToken);
            return Results.Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "report usage tracking failed (report={ReportKey})", request.ReportKey);
            return Results.Ok(new { ok = false, error = "tracking_failed" });
        }
    });

accounting.MapGet(
    "/dashboard/suggestions",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IDashboardWidgetSuggestionStore store,
        string? status,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var items = await store.GetForUserAsync(session.ActiveCompanyId, userId, status, cancellationToken);
        return Results.Ok(items);
    });

accounting.MapPost(
    "/dashboard/suggestions/generate",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IDashboardSuggestionService service,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var now = DateTimeOffset.UtcNow;
        var result = await service.GenerateAsync(session.ActiveCompanyId, userId, now.AddDays(-30), now, cancellationToken);
        return Results.Ok(result);
    });

accounting.MapPost(
    "/dashboard/suggestions/{id:guid}/accept",
    async (
        Guid id,
        BusinessSessionContextAccessor sessionAccessor,
        IDashboardWidgetSuggestionStore store,
        IDashboardUserWidgetStore widgetStore,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var existing = await store.GetByIdAsync(session.ActiveCompanyId, id, cancellationToken);
        if (existing is null) return Results.NotFound();

        var now = DateTimeOffset.UtcNow;
        await store.UpdateStatusAsync(id, DashboardSuggestionStatus.Accepted, now, null, null, cancellationToken);

        await widgetStore.UpsertAsync(new DashboardUserWidgetRecord(
            Id: Guid.NewGuid(),
            CompanyId: existing.CompanyId,
            UserId: existing.UserId,
            WidgetKey: existing.WidgetKey,
            Title: existing.Title,
            ConfigJson: null,
            Position: null,
            Source: DashboardWidgetSource.Suggestion,
            Active: true,
            CreatedAt: now,
            UpdatedAt: now), cancellationToken);

        return Results.Ok(new { ok = true });
    });

accounting.MapPost(
    "/dashboard/suggestions/{id:guid}/dismiss",
    async (
        Guid id,
        BusinessSessionContextAccessor sessionAccessor,
        IDashboardWidgetSuggestionStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var existing = await store.GetByIdAsync(session.ActiveCompanyId, id, cancellationToken);
        if (existing is null) return Results.NotFound();

        await store.UpdateStatusAsync(id, DashboardSuggestionStatus.Dismissed, null, DateTimeOffset.UtcNow, null, cancellationToken);
        return Results.Ok(new { ok = true });
    });

accounting.MapPost(
    "/dashboard/suggestions/{id:guid}/snooze",
    async (
        Guid id,
        DashboardSnoozeHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IDashboardWidgetSuggestionStore store,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();

        var existing = await store.GetByIdAsync(session.ActiveCompanyId, id, cancellationToken);
        if (existing is null) return Results.NotFound();

        var until = request.SnoozedUntil ?? DateTimeOffset.UtcNow.AddDays(7);
        await store.UpdateStatusAsync(id, DashboardSuggestionStatus.Snoozed, null, null, until, cancellationToken);
        return Results.Ok(new { ok = true });
    });

accounting.MapGet(
    "/action-center/tasks",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IActionCenterTaskService service,
        string? status,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var statusFilter = string.IsNullOrWhiteSpace(status) ? null : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tasks = await service.GetTasksAsync(session.ActiveCompanyId, userId, statusFilter, cancellationToken);
        return Results.Ok(tasks);
    });

accounting.MapPost(
    "/action-center/regenerate",
    async (
        BusinessSessionContextAccessor sessionAccessor,
        IActionCenterTaskService service,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var result = await service.RegenerateAsync(session.ActiveCompanyId, userId, cancellationToken);
        return Results.Ok(result);
    });

accounting.MapPost(
    "/action-center/tasks/{id:guid}/start",
    async (Guid id, BusinessSessionContextAccessor sessionAccessor, IActionCenterTaskService service, CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var updated = await service.StartAsync(session.ActiveCompanyId, id, userId, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

accounting.MapPost(
    "/action-center/tasks/{id:guid}/done",
    async (Guid id, BusinessSessionContextAccessor sessionAccessor, IActionCenterTaskService service, CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var updated = await service.CompleteAsync(session.ActiveCompanyId, id, userId, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

accounting.MapPost(
    "/action-center/tasks/{id:guid}/dismiss",
    async (Guid id, BusinessSessionContextAccessor sessionAccessor, IActionCenterTaskService service, CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var updated = await service.DismissAsync(session.ActiveCompanyId, id, userId, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

accounting.MapPost(
    "/action-center/tasks/{id:guid}/snooze",
    async (
        Guid id,
        ActionCenterSnoozeHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IActionCenterTaskService service,
        CancellationToken cancellationToken) =>
    {
        var session = sessionAccessor.Current;
        if (session is null || session.ActiveCompanyId == Guid.Empty) return Results.Unauthorized();
        var userId = session.UserId == Guid.Empty ? null : (Guid?)session.UserId;
        var until = request.SnoozedUntil ?? DateTimeOffset.UtcNow.AddDays(1);
        var updated = await service.SnoozeAsync(session.ActiveCompanyId, id, userId, until, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

accounting.MapPost(
    "/fx-revaluation-batches/prepare",
    async (PrepareFxRevaluationBatchHttpRequest request, PrepareFxRevaluationBatchCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PrepareFxRevaluationBatchCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    request.BookId,
                    request.RevaluationDate,
                    new(request.TransactionCurrencyCode),
                    request.AcceptedFxSnapshotId,
                    request.IncludeAccountsReceivable,
                    request.IncludeAccountsPayable,
                    request.Memo),
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

accounting.MapPost(
    "/fx-revaluation-batches/{documentId:guid}/prepare-next-period-unwind",
    async (Guid documentId, PrepareFxRevaluationUnwindBatchHttpRequest request, PrepareFxRevaluationUnwindBatchCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PrepareFxRevaluationUnwindBatchCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.UnwindDate,
                    request.Memo),
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

accounting.MapGet(
    "/fx-revaluation-batches/{documentId:guid}/cascade-unwind-plan",
    async (Guid documentId, [AsParameters] FxRevaluationCascadeUnwindPlanQuery query, IFxRevaluationDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var plan = await repository.GetCascadeUnwindPlanAsync(
                new(query.CompanyId),
                documentId,
                cancellationToken);

            return Results.Ok(new
            {
                plan.RequestedDocumentId,
                plan.RequestedDisplayNumber,
                plan.NextDocumentId,
                plan.NextDisplayNumber,
                plan.RequestedBatchIsTail,
                ActiveRevaluationCount = plan.ActiveRevaluationChain.Count,
                ActiveRevaluationChain = plan.ActiveRevaluationChain.Select(step => new
                {
                    step.DocumentId,
                    step.DisplayNumber,
                    step.RevaluationDate,
                    step.PostedAt,
                    step.IsRequestedBatch,
                    step.IsNextStep
                })
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

accounting.MapPost(
    "/fx-revaluation-batches/{documentId:guid}/prepare-cascade-unwind",
    async (Guid documentId, PrepareFxRevaluationUnwindBatchHttpRequest request, PrepareFxRevaluationCascadeUnwindBatchCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PrepareFxRevaluationCascadeUnwindBatchCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.UnwindDate,
                    request.Memo),
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

accounting.MapPost(
    "/fx-revaluation-batches/{documentId:guid}/auto-post-cascade-unwind",
    async (Guid documentId, PrepareFxRevaluationUnwindBatchHttpRequest request, PostFxRevaluationCascadeUnwindCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostFxRevaluationCascadeUnwindCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.UnwindDate,
                    request.Memo,
                    request.IdempotencyKey),
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

accounting.MapGet(
    "/fx-revaluation-batches",
    async ([AsParameters] FxRevaluationBatchListQuery query, IFxRevaluationDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var batches = await repository.ListRecentAsync(
            new(query.CompanyId),
            query.Take ?? 50,
            cancellationToken);

        return Results.Ok(batches.Select(batch => new
        {
            batch.Id,
            batch.EntityNumber,
            batch.DisplayNumber,
            batch.Status,
            batch.BatchKind,
            batch.ReversalOfDocumentId,
            batch.BookId,
            batch.BookCode,
            batch.AccountingStandard,
            batch.RevaluationProfile,
            batch.FxRoundingPolicy,
            batch.DocumentDate,
            batch.TransactionCurrencyCode,
            batch.BaseCurrencyCode,
            batch.FxSnapshotId,
            batch.FxRate,
            batch.LineCount,
            batch.UnrealizedTotalBase,
            batch.LinkedJournalEntryId,
            batch.LinkedJournalEntryDisplayNumber,
            batch.LinkedJournalPostedAt,
            batch.CreatedAt,
            batch.UpdatedAt
        }));
    });

accounting.MapGet(
    "/fx-revaluation-batches/{documentId:guid}",
    async (Guid documentId, [AsParameters] FxRevaluationBatchLookupQuery query, IFxRevaluationDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var document = await repository.GetForPostingAsync(
                new(query.CompanyId),
                documentId,
                cancellationToken);

            if (document is null)
            {
                return Results.NotFound(new
                {
                    message = "FX revaluation batch was not found in the active company context."
                });
            }

            return Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
                EntityNumber = document.EntityNumber.Value,
                DisplayNumber = document.DisplayNumber.Value,
                document.Status,
                document.BatchKind,
                document.ReversalOfDocumentId,
                document.BookId,
                document.BookCode,
                document.AccountingStandard,
                document.RevaluationProfile,
                document.FxRoundingPolicy,
                document.DocumentDate,
                TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                BaseCurrencyCode = document.BaseCurrencyCode.Value,
                FxSnapshotId = document.FxSnapshot.SnapshotId == Guid.Empty ? (Guid?)null : document.FxSnapshot.SnapshotId,
                FxRate = document.FxSnapshot.Rate,
                FxRateType = document.FxSnapshot.RateType,
                FxQuoteBasis = document.FxSnapshot.QuoteBasis,
                FxRateUseCase = document.FxSnapshot.RateUseCase,
                FxPostingReason = document.FxSnapshot.PostingReason,
                FxRequestedDate = document.FxSnapshot.RequestedDate,
                FxEffectiveDate = document.FxSnapshot.EffectiveDate,
                FxSource = document.FxSnapshot.SourceSemantics,
                document.UnrealizedFxGainAccountId,
                document.UnrealizedFxLossAccountId,
                document.Memo,
                Lines = document.RevaluationLines.Select(line => new
                {
                    line.LineNumber,
                    line.TargetOpenItemType,
                    line.TargetOpenItemId,
                    line.TargetBalanceSide,
                    line.TargetControlAccountId,
                    line.OffsetAccountId,
                    line.PartyId,
                    line.Description,
                    line.OpenAmountTx,
                    line.CarryingAmountBase,
                    line.RevaluedAmountBase,
                    line.UnrealizedAmountBase
                })
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

accounting.MapPost(
    "/fx-revaluation-batches/{documentId:guid}/post",
    async (Guid documentId, PostFxRevaluationBatchHttpRequest request, PostFxRevaluationBatchCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostFxRevaluationBatchCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/manual-journals/{documentId:guid}",
    async (Guid documentId, [AsParameters] ManualJournalLookupQuery query, IManualJournalDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Manual journal document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.Memo,
            Lines = document.JournalLines.Select(line => new
            {
                line.LineNumber,
                line.AccountId,
                line.Description,
                line.TxDebit,
                line.TxCredit
            })
        });
    });

accounting.MapPost(
    "/manual-journals/{documentId:guid}/post",
    async (Guid documentId, PostManualJournalHttpRequest request, PostManualJournalCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostManualJournalCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/invoices/drafts/{documentId:guid}",
    async (Guid documentId, [AsParameters] InvoiceLookupQuery query, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        return document is null || (document.Status != "draft" && document.Status != "submitted")
            ? Results.NotFound(new { message = "Invoice draft or submitted invoice was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
                EntityNumber = document.EntityNumber.Value,
                DisplayNumber = document.DisplayNumber.Value,
                document.Status,
                CustomerId = document.PartyId,
                DocumentDate = document.DocumentDate,
                DueDate = document.DueDate,
                TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                BaseCurrencyCode = document.BaseCurrencyCode.Value,
                FxSnapshotId = document.FxSnapshot?.SnapshotId,
                FxRate = document.FxSnapshot?.Rate,
                FxEffectiveDate = document.FxSnapshot?.EffectiveDate,
                FxSource = document.FxSnapshot?.SourceSemantics,
                document.Memo,
                Lines = document.InvoiceLines.Select(line => new
                {
                    line.LineNumber,
                    line.RevenueAccountId,
                    line.Description,
                    line.Quantity,
                    line.UnitPrice,
                    line.LineAmount,
                    line.TaxCodeId,
                    line.TaxAmount,
                    line.ItemId,
                    line.WarehouseId,
                    line.UomCode
                })
            });
    });

accounting.MapPost(
    "/invoices/drafts",
    async (SaveInvoiceDraftHttpRequest request, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.CustomerId,
                    request.InvoiceDate,
                    request.DueDate,
                    request.TransactionCurrencyCode,
                    request.BaseCurrencyCode,
                    request.FxSnapshotId,
                    request.FxRate,
                    request.FxEffectiveDate,
                    request.FxSource,
                    request.Memo,
                    request.Lines.Select(static line => new InvoiceDraftLineSaveModel(
                        line.LineNumber,
                        line.RevenueAccountId,
                        line.Description,
                        line.Quantity,
                        line.UnitPrice,
                        line.TaxCodeId,
                        line.TaxAmount,
                        line.ItemId,
                        line.WarehouseId,
                        line.UomCode)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPut(
    "/invoices/drafts/{documentId:guid}",
    async (Guid documentId, SaveInvoiceDraftHttpRequest request, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    documentId,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.CustomerId,
                    request.InvoiceDate,
                    request.DueDate,
                    request.TransactionCurrencyCode,
                    request.BaseCurrencyCode,
                    request.FxSnapshotId,
                    request.FxRate,
                    request.FxEffectiveDate,
                    request.FxSource,
                    request.Memo,
                    request.Lines.Select(static line => new InvoiceDraftLineSaveModel(
                        line.LineNumber,
                        line.RevenueAccountId,
                        line.Description,
                        line.Quantity,
                        line.UnitPrice,
                        line.TaxCodeId,
                        line.TaxAmount,
                        line.ItemId,
                        line.WarehouseId,
                        line.UomCode)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/invoices/drafts/{documentId:guid}/submit",
    async (Guid documentId, SubmitBillDraftHttpRequest request, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SubmitDraftAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/invoices/{documentId:guid}",
    async (Guid documentId, [AsParameters] InvoiceLookupQuery query, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Invoice document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            document.DueDate,
            CustomerId = document.PartyId,
            ReceivableAccountId = document.ReceivableAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.SubtotalAmount,
            document.TaxAmount,
            document.TotalAmount,
            document.Memo,
            Lines = document.InvoiceLines.Select(line => new
            {
                line.LineNumber,
                line.RevenueAccountId,
                line.Description,
                line.Quantity,
                line.UnitPrice,
                line.LineAmount,
                line.TaxAmount,
                line.PayableTaxAccountId
            })
        });
    });

accounting.MapPost(
    "/invoices/{documentId:guid}/post",
    async (Guid documentId, PostInvoiceHttpRequest request, PostInvoiceCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostInvoiceCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/credit-notes/drafts/{documentId:guid}",
    async (Guid documentId, [AsParameters] CreditNoteLookupQuery query, ICreditNoteDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(new(query.CompanyId), documentId, cancellationToken);
        return document is null || document.Status != "draft"
            ? Results.NotFound(new { message = "Credit note draft was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
                EntityNumber = document.EntityNumber.Value,
                DisplayNumber = document.DisplayNumber.Value,
                document.Status,
                CustomerId = document.PartyId,
                DocumentDate = document.DocumentDate,
                DueDate = document.DueDate,
                TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                BaseCurrencyCode = document.BaseCurrencyCode.Value,
                FxSnapshotId = document.FxSnapshot?.SnapshotId,
                FxRate = document.FxSnapshot?.Rate,
                FxEffectiveDate = document.FxSnapshot?.EffectiveDate,
                FxSource = document.FxSnapshot?.SourceSemantics,
                document.Memo,
                Lines = document.CreditNoteLines.Select(line => new
                {
                    line.LineNumber,
                    line.RevenueAccountId,
                    line.Description,
                    line.Quantity,
                    line.UnitPrice,
                    line.LineAmount,
                    line.TaxCodeId,
                    line.TaxAmount
                })
            });
    });

accounting.MapPost(
    "/credit-notes/drafts",
    async (SaveCreditNoteDraftHttpRequest request, ICreditNoteDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new CreditNoteDraftSaveModel(
                    null,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.CustomerId,
                    request.CreditNoteDate,
                    request.DueDate,
                    request.TransactionCurrencyCode,
                    request.BaseCurrencyCode,
                    request.FxSnapshotId,
                    request.FxRate,
                    request.FxEffectiveDate,
                    request.FxSource,
                    request.Memo,
                    request.Lines.Select(static line => new CreditNoteDraftLineSaveModel(
                        line.LineNumber,
                        line.RevenueAccountId,
                        line.Description,
                        line.Quantity,
                        line.UnitPrice,
                        line.TaxCodeId,
                        line.TaxAmount)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPut(
    "/credit-notes/drafts/{documentId:guid}",
    async (Guid documentId, SaveCreditNoteDraftHttpRequest request, ICreditNoteDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new CreditNoteDraftSaveModel(
                    documentId,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.CustomerId,
                    request.CreditNoteDate,
                    request.DueDate,
                    request.TransactionCurrencyCode,
                    request.BaseCurrencyCode,
                    request.FxSnapshotId,
                    request.FxRate,
                    request.FxEffectiveDate,
                    request.FxSource,
                    request.Memo,
                    request.Lines.Select(static line => new CreditNoteDraftLineSaveModel(
                        line.LineNumber,
                        line.RevenueAccountId,
                        line.Description,
                        line.Quantity,
                        line.UnitPrice,
                        line.TaxCodeId,
                        line.TaxAmount)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/credit-notes/{documentId:guid}",
    async (Guid documentId, [AsParameters] CreditNoteLookupQuery query, ICreditNoteDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Credit note document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            document.DueDate,
            CustomerId = document.PartyId,
            ReceivableAccountId = document.ReceivableAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.SubtotalAmount,
            document.TaxAmount,
            document.TotalAmount,
            document.Memo,
            Lines = document.CreditNoteLines.Select(line => new
            {
                line.LineNumber,
                line.RevenueAccountId,
                line.Description,
                line.Quantity,
                line.UnitPrice,
                line.LineAmount,
                line.TaxAmount,
                line.PayableTaxAccountId
            })
        });
    });

accounting.MapPost(
    "/credit-notes/{documentId:guid}/post",
    async (Guid documentId, PostCreditNoteHttpRequest request, PostCreditNoteCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostCreditNoteCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/bills/drafts/{documentId:guid}",
    async (Guid documentId, [AsParameters] BillLookupQuery query, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(new(query.CompanyId), documentId, cancellationToken);
        return document is null || (document.Status != "draft" && document.Status != "submitted")
            ? Results.NotFound(new { message = "Bill draft or submitted bill was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
                EntityNumber = document.EntityNumber.Value,
                DisplayNumber = document.DisplayNumber.Value,
                document.Status,
                VendorId = document.PartyId,
                DocumentDate = document.DocumentDate,
                DueDate = document.DueDate,
                TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                BaseCurrencyCode = document.BaseCurrencyCode.Value,
                FxSnapshotId = document.FxSnapshot?.SnapshotId,
                FxRate = document.FxSnapshot?.Rate,
                FxEffectiveDate = document.FxSnapshot?.EffectiveDate,
                FxSource = document.FxSnapshot?.SourceSemantics,
                document.Memo,
                Lines = document.BillLines.Select(line => new
                {
                    line.LineNumber,
                    line.ExpenseAccountId,
                    line.Description,
                    line.LineAmount,
                    line.TaxCodeId,
                    line.TaxAmount,
                    line.IsTaxRecoverable,
                    line.ItemId,
                    line.WarehouseId,
                    line.UomCode,
                    line.Quantity,
                    line.UnitCost,
                    line.PurchaseOrderId,
                    line.PurchaseOrderLineNumber
                })
            });
    });

accounting.MapPost(
    "/bills/drafts",
    async (SaveBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.BillDate,
                    request.DueDate,
                    request.TransactionCurrencyCode,
                    request.BaseCurrencyCode,
                    request.FxSnapshotId,
                    request.FxRate,
                    request.FxEffectiveDate,
                    request.FxSource,
                    request.Memo,
                    request.Lines.Select(static line => new BillDraftLineSaveModel(
                        line.LineNumber,
                        line.ExpenseAccountId,
                        line.Description,
                        line.LineAmount,
                        line.TaxCodeId,
                        line.TaxAmount,
                        line.IsTaxRecoverable,
                        line.ItemId,
                        line.WarehouseId,
                        line.UomCode,
                        line.Quantity,
                        line.UnitCost,
                        line.PurchaseOrderId,
                        line.PurchaseOrderLineNumber)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPut(
    "/bills/drafts/{documentId:guid}",
    async (Guid documentId, SaveBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new BillDraftSaveModel(
                    documentId,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.BillDate,
                    request.DueDate,
                    request.TransactionCurrencyCode,
                    request.BaseCurrencyCode,
                    request.FxSnapshotId,
                    request.FxRate,
                    request.FxEffectiveDate,
                    request.FxSource,
                    request.Memo,
                    request.Lines.Select(static line => new BillDraftLineSaveModel(
                        line.LineNumber,
                        line.ExpenseAccountId,
                        line.Description,
                        line.LineAmount,
                        line.TaxCodeId,
                        line.TaxAmount,
                        line.IsTaxRecoverable,
                        line.ItemId,
                        line.WarehouseId,
                        line.UomCode,
                        line.Quantity,
                        line.UnitCost,
                        line.PurchaseOrderId,
                        line.PurchaseOrderLineNumber)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/bills/drafts/{documentId:guid}/submit",
    async (Guid documentId, SubmitBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SubmitDraftAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/bills/drafts/{documentId:guid}/cancel",
    async (Guid documentId, SubmitBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.CancelSubmittedAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/bills/{documentId:guid}",
    async (
        Guid documentId,
        [AsParameters] BillLookupQuery query,
        IBillDocumentRepository repository,
        IReceiptGrIrApSettlementControlStore grIrSettlementStore,
        CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Bill document was not found in the active company context."
            });
        }

        var grIrSettlementSummary = await grIrSettlementStore.GetBillSettlementSummaryAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            document.DueDate,
            VendorId = document.PartyId,
            PayableAccountId = document.PayableAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.SubtotalAmount,
            document.TaxAmount,
            document.TotalAmount,
            document.Memo,
            GrIrSettlement = grIrSettlementSummary is null
                ? null
                : new
                {
                    grIrSettlementSummary.SettlementStatus,
                    grIrSettlementSummary.SettlementLineCount,
                    grIrSettlementSummary.EligibleLineCount,
                    grIrSettlementSummary.BlockedLineCount,
                    grIrSettlementSummary.BlockedGrIrNotPostedLineCount,
                    grIrSettlementSummary.BlockedBillNotPostedLineCount,
                    grIrSettlementSummary.BlockedMissingApOpenItemLineCount,
                    grIrSettlementSummary.BlockedJournalNotPostedLineCount,
                    grIrSettlementSummary.BlockedAmountExceededLineCount,
                    grIrSettlementSummary.PartiallySettledLineCount,
                    grIrSettlementSummary.SettledLineCount,
                    grIrSettlementSummary.SettlementAmountBase,
                    grIrSettlementSummary.EligibleAmountBase,
                    grIrSettlementSummary.SettledAmountBase,
                    grIrSettlementSummary.RemainingAmountBase,
                    grIrSettlementSummary.SettlementBatchCount,
                    grIrSettlementSummary.JournalNotPostedBatchCount,
                    grIrSettlementSummary.JournalPostedBatchCount,
                    grIrSettlementSummary.JournalStaleBatchCount,
                    grIrSettlementSummary.JournalInconsistentBatchCount,
                    grIrSettlementSummary.JournalReconciliationStatus,
                    grIrSettlementSummary.LastJournalRefreshedAt,
                    grIrSettlementSummary.OpenItemNotClearedBatchCount,
                    grIrSettlementSummary.OpenItemClearedBatchCount,
                    grIrSettlementSummary.OpenItemReversedBatchCount,
                    grIrSettlementSummary.OpenItemBlockedBatchCount,
                    grIrSettlementSummary.OpenItemStaleBatchCount,
                    grIrSettlementSummary.OpenItemInconsistentBatchCount,
                    grIrSettlementSummary.OpenItemClearingStatus,
                    grIrSettlementSummary.LastOpenItemClearedAt,
                    grIrSettlementSummary.LastOpenItemReversedAt,
                    grIrSettlementSummary.PurchaseVarianceLineCount,
                    grIrSettlementSummary.PurchaseVarianceCandidateLineCount,
                    grIrSettlementSummary.PurchaseVarianceNoVarianceLineCount,
                    grIrSettlementSummary.PurchaseVarianceBlockedLineCount,
                    grIrSettlementSummary.PurchaseVarianceStatus,
                    grIrSettlementSummary.PurchaseVarianceAmountBase,
                    grIrSettlementSummary.LastPurchaseVarianceRefreshedAt,
                    grIrSettlementSummary.LastRefreshedAt,
                    grIrSettlementSummary.LastSettledAt
                },
            Lines = document.BillLines.Select(line => new
            {
                line.LineNumber,
                line.ExpenseAccountId,
                line.Description,
                line.LineAmount,
                line.TaxAmount,
                line.IsTaxRecoverable,
                line.RecoverableTaxAccountId,
                line.ItemId,
                line.WarehouseId,
                line.UomCode,
                line.Quantity,
                line.UnitCost,
                line.PurchaseOrderId,
                line.PurchaseOrderLineNumber
            })
        });
    });

accounting.MapGet(
    "/bills/{documentId:guid}/receipt-matching",
    async (Guid documentId, [AsParameters] BillLookupQuery query, IBillReceiptMatchingRepository repository, CancellationToken cancellationToken) =>
    {
        var summary = await repository.GetBillLaneSummaryAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        return Results.Ok(new
        {
            summary.BillDocumentId,
            summary.BillInboundLineCount,
            summary.BillInboundQuantity,
            summary.ReceiptCount,
            summary.CoveredQuantity,
            summary.RemainingQuantity,
            summary.MatchStatus,
            summary.LatestReceiptPostedAt,
            OpenDiscrepancyCount = summary.Discrepancies.Count,
            RecentReceipts = summary.RecentReceipts.Select(receipt => new
            {
                receipt.ReceiptDocumentId,
                receipt.DisplayNumber,
                receipt.ReceiptDate,
                receipt.Status,
                receipt.ReceiptQuantity,
                receipt.MatchedQuantity,
                receipt.VendorReference,
                receipt.SourceReference,
                receipt.PostedAt
            }),
            LineSummaries = summary.LineSummaries.Select(line => new
            {
                line.BillLineNumber,
                line.ItemId,
                line.ItemCode,
                line.ItemName,
                line.WarehouseId,
                line.WarehouseCode,
                line.WarehouseName,
                line.UomCode,
                line.BillQuantity,
                line.CoveredQuantity,
                line.RemainingQuantity,
                line.ReceiptCount,
                line.MatchStatus
            }),
            Discrepancies = summary.Discrepancies.Select(discrepancy => new
            {
                discrepancy.BillDocumentId,
                discrepancy.BillLineNumber,
                discrepancy.DiscrepancyType,
                discrepancy.InvestigationStatus,
                discrepancy.ItemId,
                discrepancy.ItemCode,
                discrepancy.ItemName,
                discrepancy.WarehouseId,
                discrepancy.WarehouseCode,
                discrepancy.WarehouseName,
                discrepancy.UomCode,
                discrepancy.BillQuantity,
                discrepancy.CoveredQuantity,
                discrepancy.RemainingQuantity,
                discrepancy.Summary,
                discrepancy.FirstDetectedAt,
                discrepancy.LastDetectedAt
            })
        });
    });

accounting.MapPost(
    "/bills/{documentId:guid}/post",
    async (Guid documentId, PostBillHttpRequest request, PostBillCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostBillCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/purchase-orders",
    async (
        [AsParameters] PurchaseOrderListQuery query,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        var documents = await repository.ListAsync(new(query.CompanyId), query.Take ?? 50, cancellationToken);
        var summaries = await repository.GetThreeQuantitySummariesAsync(
            new(query.CompanyId),
            documents.Select(static document => document.DocumentId).ToArray(),
            cancellationToken);

        return Results.Ok(documents.Select(document => new
        {
            EstimatedAmount = CalculatePurchaseOrderListEstimatedAmount(document),
            document.DocumentId,
            document.EntityNumber,
            document.DisplayNumber,
            document.Status,
            document.VendorId,
            document.OrderDate,
            document.ExpectedDate,
            document.LineCount,
            document.TotalOrderedQuantity,
            document.VendorReference,
            document.Memo,
            document.CreatedAt,
            document.UpdatedAt,
            document.ApprovedAt,
            document.IssuedAt,
            document.ClosedAt,
            document.CancelledAt,
            document.AmendmentStartedAt,
            AnchorGovernance = new
            {
                AllowsNewAnchors = PurchaseOrderAnchorPolicy.AllowsNewAnchor(document.Status),
                Summary = PurchaseOrderAnchorPolicy.BuildAnchorStatusSummary(document.Status)
            },
            ApprovalAuthority = BuildPurchaseOrderApprovalAuthoritySummary(CalculatePurchaseOrderListEstimatedAmount(document)),
            ThreeQuantity = summaries.TryGetValue(document.DocumentId, out var summary) ? summary : null
        }));
    });

accounting.MapGet(
    "/purchase-orders/approval-requests",
    async (
        [AsParameters] PurchaseOrderApprovalRequestListQuery query,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        var requests = await repository.ListApprovalRequestsAsync(
            new(query.CompanyId),
            query.Take ?? 50,
            query.IncludeClosed ?? false,
            cancellationToken);

        return Results.Ok(requests);
    });

accounting.MapGet(
    "/purchase-orders/{documentId:guid}",
    async (
        Guid documentId,
        [AsParameters] PurchaseOrderLookupQuery query,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        var document = await repository.GetAsync(new(query.CompanyId), documentId, cancellationToken);
        if (document is null)
        {
            return Results.NotFound(new { message = "Purchase order document was not found in the active company context." });
        }

        var summary = await repository.GetThreeQuantitySummaryAsync(new(query.CompanyId), documentId, cancellationToken);
        var purchaseVarianceSummary = await repository.GetPurchaseVarianceSummaryAsync(new(query.CompanyId), documentId, cancellationToken);
        var estimatedAmount = CalculatePurchaseOrderDocumentEstimatedAmount(document);
        return Results.Ok(new
        {
            EstimatedAmount = estimatedAmount,
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.VendorId,
            document.OrderDate,
            document.ExpectedDate,
            document.VendorReference,
            document.Memo,
            document.ApprovedAt,
            document.IssuedAt,
            document.ClosedAt,
            document.CancelledAt,
            document.AmendmentStartedAt,
            AnchorGovernance = new
            {
                AllowsNewAnchors = PurchaseOrderAnchorPolicy.AllowsNewAnchor(document.Status),
                Summary = PurchaseOrderAnchorPolicy.BuildAnchorStatusSummary(document.Status)
            },
            ApprovalAuthority = BuildPurchaseOrderApprovalAuthoritySummary(estimatedAmount),
            ThreeQuantity = summary,
            PurchaseVariance = purchaseVarianceSummary,
            Lines = document.PurchaseOrderLines.Select(line => new
            {
                line.LineNumber,
                line.ItemId,
                line.OrderedQuantity,
                line.UomCode,
                line.Description,
                line.UnitCost
            })
        });
    });

accounting.MapGet(
    "/purchase-orders/{documentId:guid}/lifecycle-audit",
    async (
        Guid documentId,
        [AsParameters] PurchaseOrderLifecycleAuditQuery query,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        var document = await repository.GetAsync(new(query.CompanyId), documentId, cancellationToken);
        if (document is null)
        {
            return Results.NotFound(new { message = "Purchase order document was not found in the active company context." });
        }

        var entries = await repository.ListLifecycleAuditAsync(
            new(query.CompanyId),
            documentId,
            query.Take ?? 50,
            cancellationToken);

        return Results.Ok(entries);
    });

accounting.MapGet(
    "/purchase-orders/{documentId:guid}/approval-request",
    async (
        Guid documentId,
        [AsParameters] PurchaseOrderLookupQuery query,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        var request = await repository.GetLatestApprovalRequestAsync(new(query.CompanyId), documentId, cancellationToken);
        return request is null
            ? Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." })
            : Results.Ok(request);
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/approval-request",
    async (
        Guid documentId,
        RequestPurchaseOrderApprovalHttpRequest request,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.RequestApprovalAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                request.Reason,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/approval-request/{requestId:guid}/submit",
    async (
        Guid documentId,
        Guid requestId,
        SubmitPurchaseOrderApprovalRequestHttpRequest request,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SubmitApprovalRequestAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                requestId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." })
                : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/approval-request/{requestId:guid}/reject",
    async (
        Guid documentId,
        Guid requestId,
        RejectPurchaseOrderApprovalRequestHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        var current = await repository.GetLatestApprovalRequestAsync(new(request.CompanyId), documentId, cancellationToken);
        if (current is null || current.RequestId != requestId)
        {
            return Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." });
        }

        var authorityBlock = RequirePurchaseOrderApprovalAuthority(
            sessionAccessor.Current,
            "reject_approval_request",
            current.EstimatedAmount);
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await repository.RejectApprovalRequestAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                requestId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." })
                : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/drafts",
    async (SavePurchaseOrderDraftHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new PurchaseOrderDraftSaveModel(
                    null,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.OrderDate,
                    request.ExpectedDate,
                    request.VendorReference,
                    request.Memo,
                    request.Lines.Select(static line => new PurchaseOrderDraftLineSaveModel(
                        line.LineNumber,
                        line.ItemId,
                        line.OrderedQuantity,
                        line.UomCode,
                        line.Description,
                        line.UnitCost)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPut(
    "/purchase-orders/drafts/{documentId:guid}",
    async (Guid documentId, SavePurchaseOrderDraftHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new PurchaseOrderDraftSaveModel(
                    documentId,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.OrderDate,
                    request.ExpectedDate,
                    request.VendorReference,
                    request.Memo,
                    request.Lines.Select(static line => new PurchaseOrderDraftLineSaveModel(
                        line.LineNumber,
                        line.ItemId,
                        line.OrderedQuantity,
                        line.UomCode,
                        line.Description,
                        line.UnitCost)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/approve",
    async (Guid documentId, ApprovePurchaseOrderHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetAsync(new(request.CompanyId), documentId, cancellationToken);
        if (document is null)
        {
            return Results.NotFound(new { message = "Purchase order document was not found in the active company context." });
        }

        var authorityBlock = RequirePurchaseOrderApprovalAuthority(
            sessionAccessor.Current,
            "approve",
            CalculatePurchaseOrderDocumentEstimatedAmount(document));
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await repository.ApproveAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/approval/reverse",
    async (
        Guid documentId,
        ReversePurchaseOrderApprovalHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequirePurchaseOrderApprovalReversalAuthority(
            sessionAccessor.Current,
            "reverse_approval");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await repository.ReverseApprovalAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/issue",
    async (Guid documentId, IssuePurchaseOrderHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequirePurchaseOrderReleaseAuthority(sessionAccessor.Current, "release");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await repository.IssueAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/reopen-for-amendment",
    async (Guid documentId, ReopenPurchaseOrderForAmendmentHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequirePurchaseOrderAmendmentAuthority(sessionAccessor.Current, "reopen_for_amendment");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await repository.ReopenForAmendmentAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/close",
    async (Guid documentId, ClosePurchaseOrderHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequirePurchaseOrderCloseAuthority(sessionAccessor.Current, "close");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await repository.CloseAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/cancel",
    async (Guid documentId, CancelPurchaseOrderHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequirePurchaseOrderCancelAuthority(sessionAccessor.Current, "cancel");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await repository.CancelAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/quantity-discrepancies/refresh",
    async (Guid documentId, RefreshPurchaseOrderQuantityDiscrepanciesHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var summary = await repository.RefreshQuantityDiscrepanciesAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return summary is null
                ? Results.NotFound(new { message = "Purchase order document was not found in the active company context." })
                : Results.Ok(summary);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/quantity-discrepancies/review",
    async (Guid documentId, ReviewPurchaseOrderQuantityDiscrepancyHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var summary = await repository.ReviewQuantityDiscrepancyAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                request.PurchaseOrderLineNumber,
                request.DiscrepancyType,
                request.InvestigationStatus,
                request.ReviewNote,
                cancellationToken);

            return summary is null
                ? Results.NotFound(new { message = "Purchase order document was not found in the active company context." })
                : Results.Ok(summary);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { code = "invalid_operation", message = ex.Message });
        }
    });

accounting.MapGet(
    "/receipts",
    async (
        [AsParameters] ReceiptListQuery query,
        IReceiptDocumentRepository repository,
        IReceiptInventoryActivationStore activationStore,
        IReceiptInventoryValuationStore valuationStore,
        IReceiptInventoryCostLayerEmissionStore emissionStore,
        IReceiptGrIrBridgeStore grIrBridgeStore,
        IReceiptGrIrApSettlementControlStore grIrSettlementStore,
        CancellationToken cancellationToken) =>
    {
        var documents = await repository.ListAsync(
            new(query.CompanyId),
            query.Take ?? 50,
            cancellationToken);
        var activationSummaries = await activationStore.GetReceiptActivationSummariesAsync(
            query.CompanyId,
            documents.Select(static document => document.DocumentId).ToArray(),
            cancellationToken);
        var valuationSummaries = await valuationStore.GetReceiptValuationSummariesAsync(
            query.CompanyId,
            documents.Select(static document => document.DocumentId).ToArray(),
            cancellationToken);
        var emissionSummaries = await emissionStore.GetReceiptCostLayerEmissionSummariesAsync(
            query.CompanyId,
            documents.Select(static document => document.DocumentId).ToArray(),
            cancellationToken);
        var emissionReconciliationSummaries = await emissionStore.GetReceiptCostLayerEmissionReconciliationSummariesAsync(
            query.CompanyId,
            documents.Select(static document => document.DocumentId).ToArray(),
            cancellationToken);
        var grIrBridgeSummaries = await grIrBridgeStore.GetReceiptGrIrBridgeSummariesAsync(
            query.CompanyId,
            documents.Select(static document => document.DocumentId).ToArray(),
            cancellationToken);
        var grIrSettlementSummaries = await grIrSettlementStore.GetReceiptSettlementSummariesAsync(
            new(query.CompanyId),
            documents.Select(static document => document.DocumentId).ToArray(),
            cancellationToken);

        return Results.Ok(documents.Select(document => new
        {
            document.DocumentId,
            document.EntityNumber,
            document.DisplayNumber,
            document.Status,
            document.VendorId,
            document.WarehouseId,
            document.ReceiptDate,
            document.LineCount,
            document.TotalQuantity,
            document.VendorReference,
            document.SourceReference,
            document.Memo,
            document.CreatedAt,
            document.UpdatedAt,
            document.PostedAt,
            InventoryActivation = activationSummaries.TryGetValue(document.DocumentId, out var summary)
                ? new
                {
                    summary.ReceiptStatus,
                    summary.ActivationStatus,
                    summary.InventoryDocumentId,
                    summary.ReceiptLineCount,
                    summary.ActivatedLineCount,
                    summary.TotalQuantity,
                    summary.ActivatedQuantity,
                    summary.ActivatedAt,
                    summary.LastFailureMessage,
                    summary.LastFailureAt
                }
                : null,
            InventoryValuation = valuationSummaries.TryGetValue(document.DocumentId, out var valuationSummary)
                ? new
                {
                    valuationSummary.ValuationStatus,
                    valuationSummary.ActivatedQuantity,
                    valuationSummary.BillCoveredQuantity,
                    valuationSummary.ValuedQuantity,
                    valuationSummary.UnvaluedQuantity,
                    valuationSummary.ValuationLineCount,
                    valuationSummary.ValuationAmountBase,
                    valuationSummary.LastValuedAt
                }
                : null,
            InventoryCostLayerEmission = emissionSummaries.TryGetValue(document.DocumentId, out var emissionSummary)
                ? new
                {
                    emissionSummary.EmissionStatus,
                    emissionSummary.ActivatedQuantity,
                    emissionSummary.ValuationBackedQuantity,
                    emissionSummary.EmissionEligibleQuantity,
                    emissionSummary.EmittedQuantity,
                    emissionSummary.UnemittedQuantity,
                    emissionSummary.EmissionLineCount,
                    emissionSummary.EmittedCostBase,
                    emissionSummary.LastEmittedAt
                }
                : null,
            InventoryCostLayerEmissionReconciliation = emissionReconciliationSummaries.TryGetValue(document.DocumentId, out var reconciliationSummary)
                ? new
                {
                    reconciliationSummary.ReconciliationStatus,
                    reconciliationSummary.EmissionLineCount,
                    reconciliationSummary.CostLayerCount,
                    reconciliationSummary.MissingCostLayerCount,
                    reconciliationSummary.OrphanCostLayerCount,
                    reconciliationSummary.EmittedQuantity,
                    reconciliationSummary.CostLayerQuantity,
                    reconciliationSummary.EmittedCostBase,
                    reconciliationSummary.CostLayerOriginalCostBase,
                    reconciliationSummary.LastEmittedAt
                }
                : null,
            GrIrBridge = grIrBridgeSummaries.TryGetValue(document.DocumentId, out var grIrBridgeSummary)
                ? new
                {
                    grIrBridgeSummary.BridgeStatus,
                    grIrBridgeSummary.BridgeLineCount,
                    grIrBridgeSummary.EligibleLineCount,
                    grIrBridgeSummary.BlockedReconciliationLineCount,
                    grIrBridgeSummary.BlockedVarianceLineCount,
                    grIrBridgeSummary.PostedLineCount,
                    grIrBridgeSummary.BridgeQuantity,
                    grIrBridgeSummary.BridgeAmountBase,
                    grIrBridgeSummary.EligibleAmountBase,
                    grIrBridgeSummary.BlockedAmountBase,
                    grIrBridgeSummary.PostedAmountBase,
                    grIrBridgeSummary.JournalEntryId,
                    grIrBridgeSummary.JournalEntryDisplayNumber,
                    grIrBridgeSummary.LastPostedAt,
                    grIrBridgeSummary.LastRefreshedAt
                }
                : null,
            GrIrSettlement = grIrSettlementSummaries.TryGetValue(document.DocumentId, out var grIrSettlementSummary)
                ? new
                {
                    grIrSettlementSummary.SettlementStatus,
                    grIrSettlementSummary.SettlementLineCount,
                    grIrSettlementSummary.EligibleLineCount,
                    grIrSettlementSummary.BlockedLineCount,
                    grIrSettlementSummary.BlockedGrIrNotPostedLineCount,
                    grIrSettlementSummary.BlockedBillNotPostedLineCount,
                    grIrSettlementSummary.BlockedMissingApOpenItemLineCount,
                    grIrSettlementSummary.BlockedJournalNotPostedLineCount,
                    grIrSettlementSummary.BlockedAmountExceededLineCount,
                    grIrSettlementSummary.PartiallySettledLineCount,
                    grIrSettlementSummary.SettledLineCount,
                    grIrSettlementSummary.SettlementAmountBase,
                    grIrSettlementSummary.EligibleAmountBase,
                    grIrSettlementSummary.SettledAmountBase,
                    grIrSettlementSummary.RemainingAmountBase,
                    grIrSettlementSummary.SettlementBatchCount,
                    grIrSettlementSummary.JournalNotPostedBatchCount,
                    grIrSettlementSummary.JournalPostedBatchCount,
                    grIrSettlementSummary.JournalStaleBatchCount,
                    grIrSettlementSummary.JournalInconsistentBatchCount,
                    grIrSettlementSummary.JournalReconciliationStatus,
                    grIrSettlementSummary.LastJournalRefreshedAt,
                    grIrSettlementSummary.OpenItemNotClearedBatchCount,
                    grIrSettlementSummary.OpenItemClearedBatchCount,
                    grIrSettlementSummary.OpenItemReversedBatchCount,
                    grIrSettlementSummary.OpenItemBlockedBatchCount,
                    grIrSettlementSummary.OpenItemStaleBatchCount,
                    grIrSettlementSummary.OpenItemInconsistentBatchCount,
                    grIrSettlementSummary.OpenItemClearingStatus,
                    grIrSettlementSummary.LastOpenItemClearedAt,
                    grIrSettlementSummary.LastOpenItemReversedAt,
                    grIrSettlementSummary.PurchaseVarianceLineCount,
                    grIrSettlementSummary.PurchaseVarianceCandidateLineCount,
                    grIrSettlementSummary.PurchaseVarianceNoVarianceLineCount,
                    grIrSettlementSummary.PurchaseVarianceBlockedLineCount,
                    grIrSettlementSummary.PurchaseVarianceStatus,
                    grIrSettlementSummary.PurchaseVarianceAmountBase,
                    grIrSettlementSummary.LastPurchaseVarianceRefreshedAt,
                    grIrSettlementSummary.LastRefreshedAt,
                    grIrSettlementSummary.LastSettledAt
                }
                : null
        }));
    });

accounting.MapGet(
    "/receipts/grir-clearing-account-policy",
    async (
        [AsParameters] ReceiptLookupQuery query,
        IReceiptGrIrClearingAccountPolicyRepository repository,
        CancellationToken cancellationToken) =>
    {
        var accountId = await repository.GetDefaultGrIrClearingAccountIdAsync(
            new(query.CompanyId),
            cancellationToken);

        return Results.Ok(new
        {
            query.CompanyId,
            GrIrClearingAccountId = accountId
        });
    });

accounting.MapPost(
    "/receipts/grir-clearing-account-policy",
    async (
        SaveReceiptGrIrClearingAccountPolicyHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        IReceiptGrIrClearingAccountPolicyRepository repository,
        CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequireGrIrClearingAccountPolicyManagementAuthority(
            sessionAccessor.Current,
            "save");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            await repository.SaveDefaultGrIrClearingAccountAsync(
                new(request.CompanyId),
                new(request.UserId),
                request.GrIrClearingAccountId,
                cancellationToken);

            return Results.Ok(new
            {
                request.CompanyId,
                request.GrIrClearingAccountId
            });
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/receipts/{documentId:guid}",
    async (
        Guid documentId,
        [AsParameters] ReceiptLookupQuery query,
        IReceiptDocumentRepository repository,
        IReceiptInventoryActivationStore activationStore,
        IReceiptInventoryValuationStore valuationStore,
        IReceiptInventoryCostLayerEmissionStore emissionStore,
        IReceiptGrIrBridgeStore grIrBridgeStore,
        IReceiptGrIrApSettlementControlStore grIrSettlementStore,
        CancellationToken cancellationToken) =>
    {
        var document = await repository.GetAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);
        var activationSummary = await activationStore.GetReceiptActivationSummaryAsync(
            query.CompanyId,
            documentId,
            cancellationToken);
        var valuationSummary = await valuationStore.GetReceiptValuationSummaryAsync(
            query.CompanyId,
            documentId,
            cancellationToken);
        var emissionSummary = await emissionStore.GetReceiptCostLayerEmissionSummaryAsync(
            query.CompanyId,
            documentId,
            cancellationToken);
        var emissionReconciliationSummary = await emissionStore.GetReceiptCostLayerEmissionReconciliationSummaryAsync(
            query.CompanyId,
            documentId,
            cancellationToken);
        var grIrBridgeSummary = await grIrBridgeStore.GetReceiptGrIrBridgeSummaryAsync(
            query.CompanyId,
            documentId,
            cancellationToken);
        var grIrSettlementSummary = await grIrSettlementStore.GetReceiptSettlementSummaryAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        return document is null
            ? Results.NotFound(new { message = "Receipt document was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
                EntityNumber = document.EntityNumber.Value,
                DisplayNumber = document.DisplayNumber.Value,
                document.SourceType,
                document.Status,
                document.VendorId,
                document.WarehouseId,
                document.ReceiptDate,
                document.VendorReference,
                document.SourceReference,
                document.Memo,
                document.PostedAt,
                InventoryActivation = activationSummary is null
                    ? null
                    : new
                    {
                        activationSummary.ReceiptStatus,
                        activationSummary.ActivationStatus,
                        activationSummary.InventoryDocumentId,
                        activationSummary.ReceiptLineCount,
                        activationSummary.ActivatedLineCount,
                        activationSummary.TotalQuantity,
                        activationSummary.ActivatedQuantity,
                        activationSummary.ActivatedAt,
                        activationSummary.LastFailureMessage,
                        activationSummary.LastFailureAt
                    },
                InventoryValuation = valuationSummary is null
                    ? null
                    : new
                    {
                        valuationSummary.ValuationStatus,
                        valuationSummary.ActivatedQuantity,
                        valuationSummary.BillCoveredQuantity,
                        valuationSummary.ValuedQuantity,
                        valuationSummary.UnvaluedQuantity,
                        valuationSummary.ValuationLineCount,
                        valuationSummary.ValuationAmountBase,
                        valuationSummary.LastValuedAt
                    },
                InventoryCostLayerEmission = emissionSummary is null
                    ? null
                    : new
                    {
                        emissionSummary.EmissionStatus,
                        emissionSummary.ActivatedQuantity,
                        emissionSummary.ValuationBackedQuantity,
                        emissionSummary.EmissionEligibleQuantity,
                        emissionSummary.EmittedQuantity,
                        emissionSummary.UnemittedQuantity,
                        emissionSummary.EmissionLineCount,
                        emissionSummary.EmittedCostBase,
                        emissionSummary.LastEmittedAt
                    },
                InventoryCostLayerEmissionReconciliation = emissionReconciliationSummary is null
                    ? null
                    : new
                    {
                        emissionReconciliationSummary.ReconciliationStatus,
                        emissionReconciliationSummary.EmissionLineCount,
                        emissionReconciliationSummary.CostLayerCount,
                        emissionReconciliationSummary.MissingCostLayerCount,
                        emissionReconciliationSummary.OrphanCostLayerCount,
                        emissionReconciliationSummary.EmittedQuantity,
                        emissionReconciliationSummary.CostLayerQuantity,
                        emissionReconciliationSummary.EmittedCostBase,
                        emissionReconciliationSummary.CostLayerOriginalCostBase,
                        emissionReconciliationSummary.LastEmittedAt
                    },
                GrIrBridge = grIrBridgeSummary is null
                    ? null
                    : new
                    {
                        grIrBridgeSummary.BridgeStatus,
                        grIrBridgeSummary.BridgeLineCount,
                        grIrBridgeSummary.EligibleLineCount,
                        grIrBridgeSummary.BlockedReconciliationLineCount,
                        grIrBridgeSummary.BlockedVarianceLineCount,
                        grIrBridgeSummary.PostedLineCount,
                        grIrBridgeSummary.BridgeQuantity,
                        grIrBridgeSummary.BridgeAmountBase,
                        grIrBridgeSummary.EligibleAmountBase,
                        grIrBridgeSummary.BlockedAmountBase,
                        grIrBridgeSummary.PostedAmountBase,
                        grIrBridgeSummary.JournalEntryId,
                        grIrBridgeSummary.JournalEntryDisplayNumber,
                        grIrBridgeSummary.LastPostedAt,
                        grIrBridgeSummary.LastRefreshedAt
                    },
                GrIrSettlement = grIrSettlementSummary is null
                    ? null
                    : new
                    {
                        grIrSettlementSummary.SettlementStatus,
                        grIrSettlementSummary.SettlementLineCount,
                        grIrSettlementSummary.EligibleLineCount,
                        grIrSettlementSummary.BlockedLineCount,
                        grIrSettlementSummary.BlockedGrIrNotPostedLineCount,
                        grIrSettlementSummary.BlockedBillNotPostedLineCount,
                        grIrSettlementSummary.BlockedMissingApOpenItemLineCount,
                        grIrSettlementSummary.BlockedJournalNotPostedLineCount,
                        grIrSettlementSummary.BlockedAmountExceededLineCount,
                        grIrSettlementSummary.PartiallySettledLineCount,
                        grIrSettlementSummary.SettledLineCount,
                        grIrSettlementSummary.SettlementAmountBase,
                        grIrSettlementSummary.EligibleAmountBase,
                        grIrSettlementSummary.SettledAmountBase,
                        grIrSettlementSummary.RemainingAmountBase,
                        grIrSettlementSummary.SettlementBatchCount,
                        grIrSettlementSummary.JournalNotPostedBatchCount,
                        grIrSettlementSummary.JournalPostedBatchCount,
                        grIrSettlementSummary.JournalStaleBatchCount,
                        grIrSettlementSummary.JournalInconsistentBatchCount,
                        grIrSettlementSummary.JournalReconciliationStatus,
                        grIrSettlementSummary.LastJournalRefreshedAt,
                        grIrSettlementSummary.OpenItemNotClearedBatchCount,
                        grIrSettlementSummary.OpenItemClearedBatchCount,
                        grIrSettlementSummary.OpenItemReversedBatchCount,
                        grIrSettlementSummary.OpenItemBlockedBatchCount,
                        grIrSettlementSummary.OpenItemStaleBatchCount,
                        grIrSettlementSummary.OpenItemInconsistentBatchCount,
                        grIrSettlementSummary.OpenItemClearingStatus,
                        grIrSettlementSummary.LastOpenItemClearedAt,
                        grIrSettlementSummary.LastOpenItemReversedAt,
                        grIrSettlementSummary.PurchaseVarianceLineCount,
                        grIrSettlementSummary.PurchaseVarianceCandidateLineCount,
                        grIrSettlementSummary.PurchaseVarianceNoVarianceLineCount,
                        grIrSettlementSummary.PurchaseVarianceBlockedLineCount,
                        grIrSettlementSummary.PurchaseVarianceStatus,
                        grIrSettlementSummary.PurchaseVarianceAmountBase,
                        grIrSettlementSummary.LastPurchaseVarianceRefreshedAt,
                        grIrSettlementSummary.LastRefreshedAt,
                        grIrSettlementSummary.LastSettledAt
                    },
                Lines = document.ReceiptLines.Select(line => new
                {
                    line.LineNumber,
                    line.ItemId,
                    line.Quantity,
                    line.UomCode,
                    line.TrackingCaptureHome,
                    line.PurchaseOrderId,
                    line.PurchaseOrderLineNumber
                })
            });
    });

accounting.MapPost(
    "/receipts/drafts",
    async (SaveReceiptDraftHttpRequest request, IReceiptDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new ReceiptDraftSaveModel(
                    null,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.WarehouseId,
                    request.ReceiptDate,
                    request.VendorReference,
                    request.SourceReference,
                    request.Memo,
                    request.Lines.Select(static line => new ReceiptDraftLineSaveModel(
                        line.LineNumber,
                        line.ItemId,
                        line.Quantity,
                        line.UomCode,
                        line.TrackingCaptureHome,
                        line.PurchaseOrderId,
                        line.PurchaseOrderLineNumber)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPut(
    "/receipts/drafts/{documentId:guid}",
    async (Guid documentId, SaveReceiptDraftHttpRequest request, IReceiptDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new ReceiptDraftSaveModel(
                    documentId,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.WarehouseId,
                    request.ReceiptDate,
                    request.VendorReference,
                    request.SourceReference,
                    request.Memo,
                    request.Lines.Select(static line => new ReceiptDraftLineSaveModel(
                        line.LineNumber,
                        line.ItemId,
                        line.Quantity,
                        line.UomCode,
                        line.TrackingCaptureHome,
                        line.PurchaseOrderId,
                        line.PurchaseOrderLineNumber)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/post",
    async (Guid documentId, PostReceiptDraftHttpRequest request, PostReceiptWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.PostAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/inventory-activation/retry",
    async (Guid documentId, PostReceiptDraftHttpRequest request, PostReceiptWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.PostAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/inventory-valuation/refresh",
    async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptInventoryValuationStore valuationStore, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await valuationStore.RefreshReceiptValuationAsync(
                request.CompanyId,
                request.UserId,
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/inventory-cost-layer-emission/emit",
    async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptInventoryCostLayerEmissionStore emissionStore, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await emissionStore.EmitReceiptCostLayersAsync(
                request.CompanyId,
                request.UserId,
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-bridge/refresh",
    async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrBridgeStore grIrBridgeStore, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(
                request.CompanyId,
                request.UserId,
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/refresh",
    async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrApSettlementControlStore grIrSettlementStore, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await grIrSettlementStore.RefreshReceiptSettlementControlAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/journal-reconciliation/refresh",
    async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrApSettlementControlStore grIrSettlementStore, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await grIrSettlementStore.RefreshReceiptSettlementJournalReconciliationAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/purchase-variance/refresh",
    async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrApSettlementControlStore grIrSettlementStore, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await grIrSettlementStore.RefreshReceiptSettlementVarianceControlAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/receipts/{documentId:guid}/grir-settlement/purchase-variance/lines",
    async (
        Guid documentId,
        [AsParameters] ReceiptLookupQuery query,
        IReceiptGrIrApSettlementControlStore grIrSettlementStore,
        CancellationToken cancellationToken) =>
    {
        var result = await grIrSettlementStore.ListReceiptPurchaseVarianceLinesAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        return Results.Ok(result);
    });

accounting.MapGet(
    "/receipts/{documentId:guid}/grir-settlement/batches",
    async (
        Guid documentId,
        [AsParameters] ReceiptLookupQuery query,
        IReceiptGrIrApSettlementControlStore grIrSettlementStore,
        CancellationToken cancellationToken) =>
    {
        var result = await grIrSettlementStore.ListReceiptSettlementBatchesAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        return Results.Ok(result);
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/execute",
    async (
        Guid documentId,
        ExecuteReceiptGrIrSettlementHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ExecuteReceiptGrIrSettlementCommandHandler handler,
        CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequireGrIrSettlementExecutionAuthority(
            sessionAccessor.Current,
            "execute");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await handler.HandleAsync(
                new ExecuteReceiptGrIrSettlementCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    request.SettlementAmountBase,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/{settlementBatchId:guid}/journal/post",
    async (
        Guid documentId,
        Guid settlementBatchId,
        PostReceiptGrIrSettlementJournalHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        PostReceiptGrIrSettlementJournalCommandHandler handler,
        CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequireGrIrSettlementExecutionAuthority(
            sessionAccessor.Current,
            "post");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await handler.HandleAsync(
                new PostReceiptGrIrSettlementJournalCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    settlementBatchId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/{settlementBatchId:guid}/ap-open-item/clear",
    async (
        Guid documentId,
        Guid settlementBatchId,
        PostReceiptDraftHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ClearReceiptGrIrSettlementOpenItemCommandHandler handler,
        CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequireGrIrSettlementExecutionAuthority(
            sessionAccessor.Current,
            "clear");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await handler.HandleAsync(
                new ClearReceiptGrIrSettlementOpenItemCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    settlementBatchId),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/{settlementBatchId:guid}/ap-open-item/reverse",
    async (
        Guid documentId,
        Guid settlementBatchId,
        PostReceiptDraftHttpRequest request,
        BusinessSessionContextAccessor sessionAccessor,
        ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler handler,
        CancellationToken cancellationToken) =>
    {
        var authorityBlock = RequireGrIrSettlementExecutionAuthority(
            sessionAccessor.Current,
            "reverse");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        try
        {
            var result = await handler.HandleAsync(
                new ReverseReceiptGrIrSettlementOpenItemClearingCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    settlementBatchId),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-bridge/post",
    async (Guid documentId, PostReceiptGrIrBridgeHttpRequest request, PostReceiptGrIrCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostReceiptGrIrCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    request.GrIrClearingAccountId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/vendor-credits/drafts/{documentId:guid}",
    async (Guid documentId, [AsParameters] VendorCreditLookupQuery query, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(new(query.CompanyId), documentId, cancellationToken);
        return document is null || document.Status != "draft"
            ? Results.NotFound(new { message = "Vendor credit draft was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
                EntityNumber = document.EntityNumber.Value,
                DisplayNumber = document.DisplayNumber.Value,
                document.Status,
                VendorId = document.PartyId,
                DocumentDate = document.DocumentDate,
                DueDate = document.DueDate,
                TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                BaseCurrencyCode = document.BaseCurrencyCode.Value,
                FxSnapshotId = document.FxSnapshot?.SnapshotId,
                FxRate = document.FxSnapshot?.Rate,
                FxEffectiveDate = document.FxSnapshot?.EffectiveDate,
                FxSource = document.FxSnapshot?.SourceSemantics,
                document.Memo,
                Lines = document.VendorCreditLines.Select(line => new
                {
                    line.LineNumber,
                    line.ExpenseAccountId,
                    line.Description,
                    line.LineAmount,
                    line.TaxCodeId,
                    line.TaxAmount,
                    line.IsTaxRecoverable
                })
            });
    });

accounting.MapPost(
    "/vendor-credits/drafts",
    async (SaveVendorCreditDraftHttpRequest request, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new VendorCreditDraftSaveModel(
                    null,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.VendorCreditDate,
                    request.DueDate,
                    request.TransactionCurrencyCode,
                    request.BaseCurrencyCode,
                    request.FxSnapshotId,
                    request.FxRate,
                    request.FxEffectiveDate,
                    request.FxSource,
                    request.Memo,
                    request.Lines.Select(static line => new VendorCreditDraftLineSaveModel(
                        line.LineNumber,
                        line.ExpenseAccountId,
                        line.Description,
                        line.LineAmount,
                        line.TaxCodeId,
                        line.TaxAmount,
                        line.IsTaxRecoverable)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPut(
    "/vendor-credits/drafts/{documentId:guid}",
    async (Guid documentId, SaveVendorCreditDraftHttpRequest request, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new VendorCreditDraftSaveModel(
                    documentId,
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.VendorCreditDate,
                    request.DueDate,
                    request.TransactionCurrencyCode,
                    request.BaseCurrencyCode,
                    request.FxSnapshotId,
                    request.FxRate,
                    request.FxEffectiveDate,
                    request.FxSource,
                    request.Memo,
                    request.Lines.Select(static line => new VendorCreditDraftLineSaveModel(
                        line.LineNumber,
                        line.ExpenseAccountId,
                        line.Description,
                        line.LineAmount,
                        line.TaxCodeId,
                        line.TaxAmount,
                        line.IsTaxRecoverable)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/vendor-credits/{documentId:guid}",
    async (Guid documentId, [AsParameters] VendorCreditLookupQuery query, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Vendor credit document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            document.DueDate,
            VendorId = document.PartyId,
            PayableAccountId = document.PayableAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.SubtotalAmount,
            document.TaxAmount,
            document.TotalAmount,
            document.Memo,
            Lines = document.VendorCreditLines.Select(line => new
            {
                line.LineNumber,
                line.ExpenseAccountId,
                line.Description,
                line.LineAmount,
                line.TaxAmount,
                line.IsTaxRecoverable,
                line.RecoverableTaxAccountId
            })
        });
    });

accounting.MapPost(
    "/vendor-credits/{documentId:guid}/post",
    async (Guid documentId, PostVendorCreditHttpRequest request, PostVendorCreditCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostVendorCreditCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/receive-payments/{documentId:guid}",
    async (Guid documentId, [AsParameters] ReceivePaymentLookupQuery query, IReceivePaymentDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Receive payment document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            CustomerId = document.PartyId,
            document.BankAccountId,
            document.ReceivableAccountId,
            document.RealizedFxGainAccountId,
            document.RealizedFxLossAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.TotalAmount,
            document.Memo,
            Lines = document.PaymentLines.Select(line => new
            {
                line.LineNumber,
                line.TargetArOpenItemId,
                line.Description,
                line.AppliedAmount,
                line.AppliedAmountBase,
                line.CarryingAmountBase
            })
        });
    });

accounting.MapPost(
    "/receive-payments/prepare",
    async (PrepareReceivePaymentDraftHttpRequest request, PrepareReceivePaymentDraftCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PrepareReceivePaymentDraftCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    request.CustomerId,
                    request.BankAccountId,
                    request.PaymentDate,
                    request.AcceptedFxSnapshotId,
                    request.Memo,
                    request.Lines.Select(line => new SettlementDraftLine(line.TargetOpenItemId, line.AppliedAmountTx)).ToArray()),
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

accounting.MapGet(
    "/customers/{customerId:guid}/open-receivables",
    async (Guid customerId, [AsParameters] OpenReceivablesLookupQuery query, IReceivePaymentDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var candidates = await repository.ListOpenReceivableCandidatesAsync(
            new(query.CompanyId),
            customerId,
            cancellationToken);

        return Results.Ok(candidates.Select(candidate => new
        {
            candidate.OpenItemId,
            candidate.SourceType,
            candidate.SourceDocumentId,
            candidate.DisplayNumber,
            candidate.DocumentDate,
            candidate.DueDate,
            candidate.DocumentCurrencyCode,
            candidate.BaseCurrencyCode,
            candidate.OriginalAmountTx,
            candidate.OpenAmountTx,
            candidate.OpenAmountBase,
            candidate.BalanceSide,
            candidate.Status
        }));
    });

accounting.MapPost(
    "/receive-payments/{documentId:guid}/post",
    async (Guid documentId, PostReceivePaymentHttpRequest request, PostReceivePaymentCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostReceivePaymentCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/credit-applications/{documentId:guid}",
    async (Guid documentId, [AsParameters] CreditApplicationLookupQuery query, ICreditApplicationDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Credit application document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            CustomerId = document.PartyId,
            ReceivableAccountId = document.ReceivableAccountId,
            document.RealizedFxGainAccountId,
            document.RealizedFxLossAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.TotalAmount,
            document.Memo,
            Lines = document.ApplicationLines.Select(line => new
            {
                line.LineNumber,
                line.SourceCreditArOpenItemId,
                line.TargetInvoiceArOpenItemId,
                line.Description,
                line.AppliedAmount,
                line.SourceCarryingAmountBase,
                line.TargetCarryingAmountBase,
                line.RealizedFxAmountBase
            })
        });
    });

accounting.MapPost(
    "/credit-applications/{documentId:guid}/post",
    async (Guid documentId, PostCreditApplicationHttpRequest request, PostCreditApplicationCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostCreditApplicationCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapPost(
    "/pay-bills/prepare",
    async (PreparePayBillDraftHttpRequest request, PreparePayBillDraftCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PreparePayBillDraftCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.BankAccountId,
                    request.PaymentDate,
                    request.AcceptedFxSnapshotId,
                    request.Memo,
                    request.Lines.Select(line => new SettlementDraftLine(line.TargetOpenItemId, line.AppliedAmountTx)).ToArray()),
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

accounting.MapGet(
    "/vendors/{vendorId:guid}/open-payables",
    async (Guid vendorId, [AsParameters] OpenPayablesLookupQuery query, IPayBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var candidates = await repository.ListOpenPayableCandidatesAsync(
            new(query.CompanyId),
            vendorId,
            cancellationToken);

        return Results.Ok(candidates.Select(candidate => new
        {
            candidate.OpenItemId,
            candidate.SourceType,
            candidate.SourceDocumentId,
            candidate.DisplayNumber,
            candidate.DocumentDate,
            candidate.DueDate,
            candidate.DocumentCurrencyCode,
            candidate.BaseCurrencyCode,
            candidate.OriginalAmountTx,
            candidate.OpenAmountTx,
            candidate.OpenAmountBase,
            candidate.BalanceSide,
            candidate.Status
        }));
    });

accounting.MapGet(
    "/pay-bills/{documentId:guid}",
    async (Guid documentId, [AsParameters] PayBillLookupQuery query, IPayBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Pay bill document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            VendorId = document.PartyId,
            document.BankAccountId,
            document.PayableAccountId,
            document.RealizedFxGainAccountId,
            document.RealizedFxLossAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.TotalAmount,
            document.Memo,
            Lines = document.PaymentLines.Select(line => new
            {
                line.LineNumber,
                line.TargetApOpenItemId,
                line.Description,
                line.AppliedAmount,
                line.AppliedAmountBase,
                line.CarryingAmountBase
            })
        });
    });

accounting.MapPost(
    "/pay-bills/{documentId:guid}/post",
    async (Guid documentId, PostPayBillHttpRequest request, PostPayBillCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostPayBillCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

accounting.MapGet(
    "/vendor-credit-applications/{documentId:guid}",
    async (Guid documentId, [AsParameters] VendorCreditApplicationLookupQuery query, IVendorCreditApplicationDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Vendor credit application document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            VendorId = document.PartyId,
            PayableAccountId = document.PayableAccountId,
            document.RealizedFxGainAccountId,
            document.RealizedFxLossAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.TotalAmount,
            document.Memo,
            Lines = document.ApplicationLines.Select(line => new
            {
                line.LineNumber,
                line.SourceVendorCreditApOpenItemId,
                line.TargetBillApOpenItemId,
                line.Description,
                line.AppliedAmount,
                line.SourceCarryingAmountBase,
                line.TargetCarryingAmountBase,
                line.RealizedFxAmountBase
            })
        });
    });

accounting.MapPost(
    "/vendor-credit-applications/{documentId:guid}/post",
    async (Guid documentId, PostVendorCreditApplicationHttpRequest request, PostVendorCreditApplicationCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostVendorCreditApplicationCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return AccountingOperationBadRequest(ex);
        }
    });

app.Run();

static IResult AccountingOperationBadRequest(InvalidOperationException exception)
{
    var code = ResolveAccountingOperationErrorCode(exception.Message);
    return Results.BadRequest(new
    {
        code,
        message = exception.Message
    });
}

static string ResolveAccountingOperationErrorCode(string message)
{
    if (message.Contains("locked by", StringComparison.OrdinalIgnoreCase)
        && message.Contains("through", StringComparison.OrdinalIgnoreCase))
    {
        return "posting_period_closed";
    }

    if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
    {
        return "not_found";
    }

    if (message.Contains("Only draft", StringComparison.OrdinalIgnoreCase))
    {
        return "invalid_document_status";
    }

    return "invalid_operation";
}

static IResult? RequireOpenItemAdjustmentApprovalAuthority(
    BusinessSessionContext? session,
    OpenItemAdjustmentRequestRecord request,
    string openItemLabel,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentApproval(
        session,
        openItemLabel,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new OpenItemAdjustmentRequestTransitionResult(
                request,
                transitionCode,
                decision.OutcomeCode,
                decision.Message),
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequireOpenItemAdjustmentAccountMappingManagementAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentAccountMappingManagement(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequireGrIrClearingAccountPolicyManagementAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluateGrIrClearingAccountPolicyManagement(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequireGrIrSettlementExecutionAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluateGrIrSettlementExecution(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderApprovalAuthority(
    BusinessSessionContext? session,
    string transitionCode,
    decimal? estimatedOrderAmount = null)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApproval(
        session,
        transitionCode,
        estimatedOrderAmount);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                estimatedOrderAmount,
                approvalThresholdAmount = BusinessApprovalAuthority.PurchaseOrderApprovalGovernanceThresholdAmount,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderReleaseAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderRelease(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderAmendmentAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderAmendment(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderApprovalReversalAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApprovalReversal(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderCloseAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderClose(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderCancelAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderCancel(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static decimal? CalculatePurchaseOrderListEstimatedAmount(PurchaseOrderDocumentListItem document) => null;

static decimal? CalculatePurchaseOrderDocumentEstimatedAmount(PurchaseOrderDocument document) =>
    document.PurchaseOrderLines.Any(static line => !line.UnitCost.HasValue)
        ? null
        : document.PurchaseOrderLines.Sum(static line => line.OrderedQuantity * line.UnitCost!.Value);

static object BuildPurchaseOrderApprovalAuthoritySummary(decimal? estimatedOrderAmount) =>
    new
    {
        EstimatedOrderAmount = estimatedOrderAmount,
        ThresholdAmount = BusinessApprovalAuthority.PurchaseOrderApprovalGovernanceThresholdAmount,
        RequiresGovernanceApproval = BusinessApprovalAuthority.RequiresPurchaseOrderGovernanceApproval(estimatedOrderAmount),
        Summary = !estimatedOrderAmount.HasValue
            ? "Estimated purchase order amount is unavailable, so the temporary threshold does not add an approval block yet."
            : BusinessApprovalAuthority.RequiresPurchaseOrderGovernanceApproval(estimatedOrderAmount)
            ? "Purchase order approval is above the temporary governance threshold and requires owner or governance authority."
            : "Purchase order approval is within the temporary approver threshold."
    };

static IResult ToCsvFileResult(ReportCsvExporter.ReportCsvFile file) =>
    Results.File(Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);

static string MapDocumentReviewSourceLabel(string sourceType) =>
    sourceType switch
    {
        "manual_journal" => "Manual Journal",
        "invoice" => "Invoice",
        "credit_note" => "Credit Note",
        "bill" => "Bill",
        "vendor_credit" => "Vendor Credit",
        "receive_payment" => "Receive Payment",
        "credit_application" => "Credit Application",
        "pay_bill" => "Pay Bill",
        "vendor_credit_application" => "Vendor Credit Application",
        "invoice_reversal" => "Invoice Reversal",
        "credit_note_reversal" => "Credit Note Reversal",
        "bill_reversal" => "Bill Reversal",
        "vendor_credit_reversal" => "Vendor Credit Reversal",
        "receive_payment_reversal" => "Receive Payment Reversal",
        "credit_application_reversal" => "Credit Application Reversal",
        "pay_bill_reversal" => "Pay Bill Reversal",
        "vendor_credit_application_reversal" => "Vendor Credit Application Reversal",
        _ => "Document"
    };

static string MapDocumentReviewCounterpartyLabel(string counterpartyRole) =>
    counterpartyRole switch
    {
        "journal" => "Journal context",
        "customer" => "Customer",
        "vendor" => "Vendor",
        _ => "Counterparty"
    };

static string MapDocumentReviewControlAccountLabel(string counterpartyRole) =>
    counterpartyRole switch
    {
        "journal" => "Balancing logic",
        "customer" => "Receivable account",
        "vendor" => "Payable account",
        _ => "Control account"
    };

static string MapDocumentReviewLineAccountLabel(string counterpartyRole) =>
    counterpartyRole switch
    {
        "journal" => "Journal account",
        "customer" => "Revenue account",
        "vendor" => "Expense account",
        _ => "Account"
    };

static string BuildInvoiceCoverageSummary(InventoryInvoiceShipmentPostingGateSnapshot snapshot) =>
    snapshot.InvoiceCoverageStatus switch
    {
        "no_inventory_handoff" => "No shipped/invoiced coverage lane is active for this invoice.",
        "no_shipment" => "Shipment truth has not started yet, so nothing is formally invoiced against shipped quantity.",
        "not_invoiced" => $"Shipment truth exists, but {snapshot.RemainingToInvoiceQuantity:N2} shipped quantity still has no formal AR coverage.",
        "partially_invoiced" => $"{snapshot.RemainingToInvoiceQuantity:N2} shipped quantity is still waiting for formal AR coverage.",
        "fully_invoiced" => "Current shipped quantity is fully covered by posted AR truth.",
        "over_invoiced" => "Posted invoice truth currently exceeds shipped quantity and should move into discrepancy review.",
        _ => "Invoice coverage truth has not been evaluated yet."
    };

static JournalEntryReviewListItemSummary MapJournalEntryReviewListItem(JournalEntryReviewListItem item) =>
    new()
    {
        Id = item.Id,
        CompanyId = item.CompanyId.Value,
        EntityNumber = item.EntityNumber,
        DisplayNumber = item.DisplayNumber,
        Status = item.Status,
        SourceType = item.SourceType,
        SourceTypeLabel = MapJournalEntrySourceTypeLabel(item.SourceType),
        SourceId = item.SourceId,
        TransactionCurrencyCode = item.TransactionCurrencyCode,
        BaseCurrencyCode = item.BaseCurrencyCode,
        TotalTxDebit = item.TotalTxDebit,
        TotalTxCredit = item.TotalTxCredit,
        TotalDebit = item.TotalDebit,
        TotalCredit = item.TotalCredit,
        LineCount = item.LineCount,
        PostedAt = item.PostedAt,
        VoidedAt = item.VoidedAt,
        ReversedAt = item.ReversedAt
    };

static JournalEntryReviewSummary MapJournalEntryReview(JournalEntryReview review) =>
    new()
    {
        Id = review.Id,
        CompanyId = review.CompanyId.Value,
        EntityNumber = review.EntityNumber,
        DisplayNumber = review.DisplayNumber,
        Status = review.Status,
        SourceType = review.SourceType,
        SourceTypeLabel = MapJournalEntrySourceTypeLabel(review.SourceType),
        SourceId = review.SourceId,
        TransactionCurrencyCode = review.TransactionCurrencyCode,
        BaseCurrencyCode = review.BaseCurrencyCode,
        ExchangeRate = review.ExchangeRate,
        ExchangeRateDate = review.ExchangeRateDate,
        ExchangeRateSource = review.ExchangeRateSource,
        FxRateSnapshotId = review.FxRateSnapshotId,
        TotalTxDebit = review.TotalTxDebit,
        TotalTxCredit = review.TotalTxCredit,
        TotalDebit = review.TotalDebit,
        TotalCredit = review.TotalCredit,
        LineCount = review.LineCount,
        PostedAt = review.PostedAt,
        VoidedAt = review.VoidedAt,
        ReversedAt = review.ReversedAt,
        CreatedByUserId = review.CreatedByUserId,
        Lines = review.Lines.Select(MapJournalEntryReviewLine).ToArray()
    };

static JournalEntryReviewLineSummary MapJournalEntryReviewLine(JournalEntryReviewLine line) =>
    new()
    {
        LineId = line.LineId,
        LineNumber = line.LineNumber,
        AccountId = line.AccountId,
        AccountCode = line.AccountCode,
        AccountName = line.AccountName,
        RootType = line.RootType,
        DetailType = line.DetailType,
        Description = line.Description,
        TxDebit = line.TxDebit,
        TxCredit = line.TxCredit,
        Debit = line.Debit,
        Credit = line.Credit,
        TaxComponentType = line.TaxComponentType,
        ControlRole = line.ControlRole,
        PartyId = line.PartyId,
        PostingRole = line.PostingRole,
        SourceLineNumber = line.SourceLineNumber
    };

static string MapJournalEntrySourceTypeLabel(string sourceType) =>
    sourceType switch
    {
        "manual_journal" => "Manual Journal",
        "invoice" => "Invoice",
        "credit_note" => "Credit Note",
        "bill" => "Bill",
        "vendor_credit" => "Vendor Credit",
        "receive_payment" => "Receive Payment",
        "credit_application" => "Credit Application",
        "pay_bill" => "Pay Bill",
        "vendor_credit_application" => "Vendor Credit Application",
        "invoice_reversal" => "Invoice Reversal",
        "credit_note_reversal" => "Credit Note Reversal",
        "bill_reversal" => "Bill Reversal",
        "vendor_credit_reversal" => "Vendor Credit Reversal",
        "receive_payment_reversal" => "Receive Payment Reversal",
        "credit_application_reversal" => "Credit Application Reversal",
        "pay_bill_reversal" => "Pay Bill Reversal",
        "vendor_credit_application_reversal" => "Vendor Credit Application Reversal",
        "fx_revaluation" => "FX Revaluation",
        _ => "Source Document"
    };

static TrialBalanceReportSummary MapTrialBalanceReport(TrialBalanceReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        IncludeZeroBalanceAccounts = report.IncludeZeroBalanceAccounts,
        AccountCount = report.AccountCount,
        TotalBalanceDebit = report.TotalBalanceDebit,
        TotalBalanceCredit = report.TotalBalanceCredit,
        IsBalanced = report.IsBalanced,
        Rows = report.Rows
            .Select(
                static row => new TrialBalanceAccountSummary
                {
                    AccountId = row.AccountId,
                    EntityNumber = row.EntityNumber,
                    Code = row.Code,
                    Name = row.Name,
                    RootType = row.RootType,
                    DetailType = row.DetailType,
                    IsActive = row.IsActive,
                    IsSystem = row.IsSystem,
                    PostedDebitTotal = row.PostedDebitTotal,
                    PostedCreditTotal = row.PostedCreditTotal,
                    BalanceDebit = row.BalanceDebit,
                    BalanceCredit = row.BalanceCredit,
                    NetBalance = row.NetBalance,
                    BalanceSide = row.BalanceSide
                })
            .ToArray()
    };

static IncomeStatementReportSummary MapIncomeStatementReport(IncomeStatementReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        DateFrom = report.DateFrom,
        DateTo = report.DateTo,
        BaseCurrencyCode = report.BaseCurrencyCode,
        IncludeZeroBalanceAccounts = report.IncludeZeroBalanceAccounts,
        AccountCount = report.AccountCount,
        TotalRevenue = report.TotalRevenue,
        TotalCostOfSales = report.TotalCostOfSales,
        GrossProfit = report.GrossProfit,
        TotalExpenses = report.TotalExpenses,
        NetIncome = report.NetIncome,
        RevenueRows = report.RevenueRows.Select(MapIncomeStatementRow).ToArray(),
        CostOfSalesRows = report.CostOfSalesRows.Select(MapIncomeStatementRow).ToArray(),
        ExpenseRows = report.ExpenseRows.Select(MapIncomeStatementRow).ToArray()
    };

static BalanceSheetReportSummary MapBalanceSheetReport(BalanceSheetReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        IncludeZeroBalanceAccounts = report.IncludeZeroBalanceAccounts,
        AccountCount = report.AccountCount,
        TotalAssets = report.TotalAssets,
        TotalLiabilities = report.TotalLiabilities,
        CurrentEarnings = report.CurrentEarnings,
        TotalEquity = report.TotalEquity,
        TotalLiabilitiesAndEquity = report.TotalLiabilitiesAndEquity,
        IsBalanced = report.IsBalanced,
        AssetRows = report.AssetRows.Select(MapBalanceSheetRow).ToArray(),
        LiabilityRows = report.LiabilityRows.Select(MapBalanceSheetRow).ToArray(),
        EquityRows = report.EquityRows.Select(MapBalanceSheetRow).ToArray()
    };

static ArAgingReportSummary MapArAgingReport(ArAgingReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        CustomerCount = report.CustomerCount,
        OpenItemCount = report.OpenItemCount,
        CurrentAmountBase = report.CurrentAmountBase,
        Days1To30AmountBase = report.Days1To30AmountBase,
        Days31To60AmountBase = report.Days31To60AmountBase,
        Days61To90AmountBase = report.Days61To90AmountBase,
        DaysOver90AmountBase = report.DaysOver90AmountBase,
        TotalOverdueAmountBase = report.TotalOverdueAmountBase,
        TotalOutstandingAmountBase = report.TotalOutstandingAmountBase,
        CustomerRows = report.CustomerRows.Select(MapArAgingCustomer).ToArray(),
        DetailRows = report.DetailRows.Select(MapArAgingRow).ToArray()
    };

static ApAgingReportSummary MapApAgingReport(ApAgingReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        VendorCount = report.VendorCount,
        OpenItemCount = report.OpenItemCount,
        CurrentAmountBase = report.CurrentAmountBase,
        Days1To30AmountBase = report.Days1To30AmountBase,
        Days31To60AmountBase = report.Days31To60AmountBase,
        Days61To90AmountBase = report.Days61To90AmountBase,
        DaysOver90AmountBase = report.DaysOver90AmountBase,
        TotalOverdueAmountBase = report.TotalOverdueAmountBase,
        TotalOutstandingAmountBase = report.TotalOutstandingAmountBase,
        VendorRows = report.VendorRows.Select(MapApAgingVendor).ToArray(),
        DetailRows = report.DetailRows.Select(MapApAgingRow).ToArray()
    };

static IncomeStatementAccountSummary MapIncomeStatementRow(IncomeStatementAccountAmount row) =>
    new()
    {
        AccountId = row.AccountId,
        EntityNumber = row.EntityNumber,
        Code = row.Code,
        Name = row.Name,
        RootType = row.RootType,
        DetailType = row.DetailType,
        IsActive = row.IsActive,
        IsSystem = row.IsSystem,
        PostedDebitTotal = row.PostedDebitTotal,
        PostedCreditTotal = row.PostedCreditTotal,
        DisplayAmount = row.DisplayAmount
    };

static BalanceSheetAccountSummary MapBalanceSheetRow(BalanceSheetAccountAmount row) =>
    new()
    {
        AccountId = row.AccountId,
        EntityNumber = row.EntityNumber,
        Code = row.Code,
        Name = row.Name,
        RootType = row.RootType,
        DetailType = row.DetailType,
        IsActive = row.IsActive,
        IsSystem = row.IsSystem,
        IsSynthetic = row.IsSynthetic,
        PostedDebitTotal = row.PostedDebitTotal,
        PostedCreditTotal = row.PostedCreditTotal,
        DisplayAmount = row.DisplayAmount
    };

static SalesCashFlowSummary MapSalesCashFlowReport(SalesCashFlowReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        Months = report.Months.Select(month => new SalesCashFlowMonthSummary
        {
            Year = month.Year,
            Month = month.Month,
            MonthStart = month.MonthStart,
            IsForecast = month.IsForecast,
            IsCurrent = month.IsCurrent,
            ReceivedAmountBase = month.ReceivedAmountBase,
            ForecastAmountBase = month.ForecastAmountBase,
        }).ToArray(),
    };

static IncomeOverTimeSummary MapIncomeOverTimeReport(IncomeOverTimeReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        FromDate = report.FromDate,
        ToDate = report.ToDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        CompareToPreviousYear = report.CompareToPreviousYear,
        Months = report.Months.Select(MapIncomeMonth).ToArray(),
        PreviousYearMonths = report.PreviousYearMonths.Select(MapIncomeMonth).ToArray(),
    };

static IncomeOverTimeMonthSummary MapIncomeMonth(IncomeOverTimeMonthBucket bucket) =>
    new()
    {
        Year = bucket.Year,
        Month = bucket.Month,
        MonthStart = bucket.MonthStart,
        AmountBase = bucket.AmountBase,
    };

static ExpenseCashOutflowSummary MapExpenseCashOutflowReport(ExpenseCashOutflowReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        Months = report.Months.Select(month => new ExpenseCashOutflowMonthSummary
        {
            Year = month.Year,
            Month = month.Month,
            MonthStart = month.MonthStart,
            IsForecast = month.IsForecast,
            IsCurrent = month.IsCurrent,
            PaidAmountBase = month.PaidAmountBase,
            ForecastAmountBase = month.ForecastAmountBase,
        }).ToArray(),
    };

static ExpenseOverTimeSummary MapExpenseOverTimeReport(ExpenseOverTimeReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        FromDate = report.FromDate,
        ToDate = report.ToDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        CompareToPreviousYear = report.CompareToPreviousYear,
        Months = report.Months.Select(MapExpenseMonth).ToArray(),
        PreviousYearMonths = report.PreviousYearMonths.Select(MapExpenseMonth).ToArray(),
    };

static ExpenseOverTimeMonthSummary MapExpenseMonth(ExpenseOverTimeMonthBucket bucket) =>
    new()
    {
        Year = bucket.Year,
        Month = bucket.Month,
        MonthStart = bucket.MonthStart,
        AmountBase = bucket.AmountBase,
    };

static ArAgingCustomerSummary MapArAgingCustomer(ArAgingCustomerBalance row) =>
    new()
    {
        CustomerId = row.CustomerId,
        CustomerEntityNumber = row.CustomerEntityNumber,
        CustomerDisplayName = row.CustomerDisplayName,
        CustomerIsActive = row.CustomerIsActive,
        OpenItemCount = row.OpenItemCount,
        OldestDueDate = row.OldestDueDate,
        CurrentAmountBase = row.CurrentAmountBase,
        Days1To30AmountBase = row.Days1To30AmountBase,
        Days31To60AmountBase = row.Days31To60AmountBase,
        Days61To90AmountBase = row.Days61To90AmountBase,
        DaysOver90AmountBase = row.DaysOver90AmountBase,
        TotalOverdueAmountBase = row.TotalOverdueAmountBase,
        TotalOutstandingAmountBase = row.TotalOutstandingAmountBase,
        OpenItems = row.OpenItems.Select(MapArAgingRow).ToArray()
    };

static ArAgingOpenItemSummary MapArAgingRow(ArAgingOpenItemAmount row) =>
    new()
    {
        OpenItemId = row.OpenItemId,
        CustomerId = row.CustomerId,
        CustomerEntityNumber = row.CustomerEntityNumber,
        CustomerDisplayName = row.CustomerDisplayName,
        CustomerIsActive = row.CustomerIsActive,
        SourceType = row.SourceType,
        SourceDocumentId = row.SourceDocumentId,
        DisplayNumber = row.DisplayNumber,
        DocumentDate = row.DocumentDate,
        DueDate = row.DueDate,
        DaysPastDue = row.DaysPastDue,
        AgingBucket = row.AgingBucket,
        DocumentCurrencyCode = row.DocumentCurrencyCode,
        BaseCurrencyCode = row.BaseCurrencyCode,
        BalanceSide = row.BalanceSide,
        Status = row.Status,
        OriginalAmountTx = row.OriginalAmountTx,
        OriginalAmountBase = row.OriginalAmountBase,
        OpenAmountTx = row.OpenAmountTx,
        OpenAmountBase = row.OpenAmountBase,
        SignedOpenAmountTx = row.SignedOpenAmountTx,
        SignedOpenAmountBase = row.SignedOpenAmountBase
    };

static ApAgingVendorSummary MapApAgingVendor(ApAgingVendorBalance row) =>
    new()
    {
        VendorId = row.VendorId,
        VendorEntityNumber = row.VendorEntityNumber,
        VendorDisplayName = row.VendorDisplayName,
        VendorIsActive = row.VendorIsActive,
        OpenItemCount = row.OpenItemCount,
        OldestDueDate = row.OldestDueDate,
        CurrentAmountBase = row.CurrentAmountBase,
        Days1To30AmountBase = row.Days1To30AmountBase,
        Days31To60AmountBase = row.Days31To60AmountBase,
        Days61To90AmountBase = row.Days61To90AmountBase,
        DaysOver90AmountBase = row.DaysOver90AmountBase,
        TotalOverdueAmountBase = row.TotalOverdueAmountBase,
        TotalOutstandingAmountBase = row.TotalOutstandingAmountBase,
        OpenItems = row.OpenItems.Select(MapApAgingRow).ToArray()
    };

static ApAgingOpenItemSummary MapApAgingRow(ApAgingOpenItemAmount row) =>
    new()
    {
        OpenItemId = row.OpenItemId,
        VendorId = row.VendorId,
        VendorEntityNumber = row.VendorEntityNumber,
        VendorDisplayName = row.VendorDisplayName,
        VendorIsActive = row.VendorIsActive,
        SourceType = row.SourceType,
        SourceDocumentId = row.SourceDocumentId,
        DisplayNumber = row.DisplayNumber,
        DocumentDate = row.DocumentDate,
        DueDate = row.DueDate,
        DaysPastDue = row.DaysPastDue,
        AgingBucket = row.AgingBucket,
        DocumentCurrencyCode = row.DocumentCurrencyCode,
        BaseCurrencyCode = row.BaseCurrencyCode,
        BalanceSide = row.BalanceSide,
        Status = row.Status,
        OriginalAmountTx = row.OriginalAmountTx,
        OriginalAmountBase = row.OriginalAmountBase,
        OpenAmountTx = row.OpenAmountTx,
        OpenAmountBase = row.OpenAmountBase,
        SignedOpenAmountTx = row.SignedOpenAmountTx,
        SignedOpenAmountBase = row.SignedOpenAmountBase
    };

internal sealed record class UnitySearchHttpQuery
{
    public Guid CompanyId { get; init; }

    public Guid? UserId { get; init; }

    public string? Context { get; init; }

    public string? Query { get; init; }

    public int? Take { get; init; }
}

internal sealed record class UnitySearchRecentHttpQuery
{
    public Guid CompanyId { get; init; }

    public Guid? UserId { get; init; }

    public string? Context { get; init; }

    public int? Take { get; init; }
}

internal sealed record class UnitySearchClickHttpRequest
{
    public Guid CompanyId { get; init; }

    public Guid UserId { get; init; }

    public string Context { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid SourceId { get; init; }
}
