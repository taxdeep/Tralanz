using Citus.Accounting.Api;
using Citus.Accounting.Api.Endpoints;
using Citus.Accounting.Api.Startup;
using static Citus.Accounting.Api.AccountingEndpointHelpers;
using static Citus.Accounting.Api.CompanyCurrencyResponseMapper;
using static Citus.Accounting.Api.InventoryItemRequestMapper;
using static Citus.Accounting.Api.Authorization.EndpointApprovalAuthorityHelpers;
using static Citus.Accounting.Api.Endpoints.Support.ReviewMappers;
using static Citus.Accounting.Api.Endpoints.Support.BusinessSessionEndpointHelpers;
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

namespace Citus.Accounting.Api.Startup;

/// <summary>
/// Per-domain service-registration extensions extracted verbatim from
/// Program.cs (P3). Each method moves a contiguous block of the original DI
/// registrations unchanged (same interfaces, implementations, and lifetimes);
/// only builder.Services / builder.Configuration are rebound to parameters,
/// one factory lambda parameter is renamed (services -> sp) to avoid
/// shadowing, and global:: qualifies top-level namespaces that would
/// otherwise bind to a Citus.* sibling outside the top-level program scope.
/// No registration was added, dropped, or re-lifetimed.
/// </summary>
public static class AccountingServiceCollectionExtensions
{
    public static IServiceCollection AddAccountingDataAccessFoundation(this IServiceCollection services, string connectionString, IConfiguration configuration)
    {
        services.AddSingleton(new PostgresConnectionFactory(connectionString));
        services.AddSingleton(new PostgreSqlConnectionFactory(connectionString));
        services.AddSingleton<PostgresExecutionContextAccessor>();
        // P0-1 (C1): shared ambient-transaction accessor. PostgresUnitOfWork publishes
        // its open (connection, transaction) here so stores in the lower
        // global::Infrastructure.PostgreSQL layer (JE lifecycle reverse, inventory issue
        // reverse) can join the outer reverse transaction instead of opening their own
        // — making the whole document-Reverse flow atomic. AsyncLocal-backed → Singleton.
        services.AddSingleton<SharedKernel.Persistence.AmbientDatabaseTransactionAccessor>();
        services.AddSingleton(new PlatformPostgresConnectionFactory(connectionString));
        services.Configure<BusinessSessionOptions>(configuration.GetSection(BusinessSessionOptions.SectionName));
        services.AddSingleton<IPlatformRuntimeStateRepository, PostgresPlatformRuntimeStateRepository>();
        services.AddSingleton<ICompanySessionContextStore, PostgreSqlCompanySessionContextStore>();
        services.AddSingleton<ICompanySessionContextWorkflow, CompanySessionContextWorkflow>();
        return services;
    }

    public static IServiceCollection AddInventorySubledger(this IServiceCollection services)
    {
        // M4 (AUDIT_2026-05-20 P2-10): inventory receipt UoW + ambient
        // execution-context accessor. The accessor wraps an AsyncLocal so
        // scope is intrinsic to the async flow, not the DI scope —
        // Singleton is correct. The UoW depends on the accessor + factory
        // and is also stateless beyond those.
        services.AddSingleton<InventoryReceiptExecutionContextAccessor>();
        services.AddSingleton<IInventoryReceiptUnitOfWork, PostgreSqlInventoryReceiptUnitOfWork>();
        services.AddSingleton<IInventoryFoundationStore, PostgreSqlInventoryFoundationStore>();
        services.AddSingleton<IInventoryModuleActivationStore, PostgresInventoryModuleActivationStore>();
        services.AddSingleton<IInventoryReceiptStore, PostgreSqlInventoryReceiptStore>();
        services.AddSingleton<IReceiptInventoryActivationStore, PostgreSqlReceiptInventoryActivationStore>();
        services.AddSingleton<IReceiptInventoryValuationStore, PostgreSqlReceiptInventoryValuationStore>();
        services.AddSingleton<IReceiptInventoryCostLayerEmissionStore, PostgreSqlReceiptInventoryCostLayerEmissionStore>();
        services.AddSingleton<IReceiptGrIrBridgeStore, PostgreSqlReceiptGrIrBridgeStore>();
        services.AddSingleton<IInventoryIssueStore, PostgreSqlInventoryIssueStore>();
        services.AddSingleton<IInventoryShipmentStore, PostgreSqlInventoryShipmentStore>();
        // P0-3b-1 (AUDIT_2026-05-20 C3): inventory adjustment GL handler.
        // PostgreSqlInventoryAdjustmentStore now requires the GL poster so the
        // Adjustment Gain/Loss + Approved Write-off paths can emit their Dr/Cr
        // JE in the same tx as the subledger writes. Sibling Workflow stays
        // unregistered until an API surface lands; the store registration is
        // the integration point for the upcoming PR.
        services.AddSingleton<IInventoryAdjustmentGlPoster, PostgreSqlInventoryAdjustmentGlPoster>();
        services.AddSingleton<IInventoryAdjustmentStore, PostgreSqlInventoryAdjustmentStore>();
        // P0-3b-2 (AUDIT_2026-05-20 C3): manufacturing GL handler. Same DI
        // shape as the adjustment poster from P0-3b-1: register both the
        // poster and the store so a future API surface can resolve the chain
        // without further wiring.
        services.AddSingleton<IInventoryManufacturingGlPoster, PostgreSqlInventoryManufacturingGlPoster>();
        services.AddSingleton<IInventoryManufacturingStore, PostgreSqlInventoryManufacturingStore>();
        // P0-3b-3 (AUDIT_2026-05-20 C3 final closure): transfer GL handler.
        // Single poster handles both Ship and Receive legs (distinguished by
        // the request's Leg enum, idempotent per leg via distinct source_types).
        services.AddSingleton<IInventoryTransferGlPoster, PostgreSqlInventoryTransferGlPoster>();
        services.AddSingleton<IInventoryTransferStore, PostgreSqlInventoryTransferStore>();
        return services;
    }

    public static IServiceCollection AddBusinessSessionServices(this IServiceCollection services)
    {
        services.AddSingleton(
            static sp => new BusinessSessionDirectory(
                sp.GetRequiredService<IOptions<BusinessSessionOptions>>(),
                sp.GetService<ICompanySessionContextWorkflow>()));
        services.AddScoped<BusinessSessionContextAccessor>();
        services.AddSingleton<BusinessSessionRequestReader>();
        services.AddSingleton<BusinessRequestContractGuard>();
        services.AddSingleton<BusinessRouteGuard>();
        return services;
    }

    public static IServiceCollection AddAccountingDocumentRepositories(this IServiceCollection services)
    {
        services.AddScoped<IManualJournalDocumentRepository, PostgresManualJournalDocumentRepository>();
        services.AddScoped<IInvoiceDocumentRepository, PostgresInvoiceDocumentRepository>();
        services.AddScoped<ISalesReceiptDocumentRepository, PostgresSalesReceiptDocumentRepository>();
        services.AddScoped<IRefundReceiptDocumentRepository, PostgresRefundReceiptDocumentRepository>();
        services.AddScoped<IBankTransferDocumentRepository, PostgresBankTransferDocumentRepository>();
        services.AddScoped<IBankDepositDocumentRepository, PostgresBankDepositDocumentRepository>();
        services.AddScoped<ITaxReturnDocumentRepository, PostgresTaxReturnDocumentRepository>();
        services.AddScoped<ICreditNoteDocumentRepository, PostgresCreditNoteDocumentRepository>();
        services.AddScoped<IBillDocumentRepository, PostgresBillDocumentRepository>();
        services.AddScoped<IBillReceiptMatchingRepository, PostgresBillReceiptMatchingRepository>();
        services.AddScoped<IReceiptDocumentRepository, PostgresReceiptDocumentRepository>();
        services.AddScoped<IPurchaseOrderDocumentRepository, PostgresPurchaseOrderDocumentRepository>();
        services.AddScoped<IVendorCreditDocumentRepository, PostgresVendorCreditDocumentRepository>();
        services.AddScoped<IReceivePaymentDocumentRepository, PostgresReceivePaymentDocumentRepository>();
        // Self-heals dev / test DBs to the customer_deposits + extra_deposit_amount
        // shape introduced for the overpay → Customer Deposit feature. Production
        // runs the canonical CITUS_POSTGRESQL_MIGRATION_DRAFT.sql; this is just a
        // safety net so app start works without a manual migration step.
        services.AddSingleton<PostgresCustomerDepositSchemaBootstrap>();
        services.AddSingleton<PostgresV1WriteFlowSchemaBootstrap>();
        services.AddScoped<ICreditApplicationDocumentRepository, PostgresCreditApplicationDocumentRepository>();
        services.AddScoped<IPayBillDocumentRepository, PostgresPayBillDocumentRepository>();
        services.AddScoped<IVendorCreditApplicationDocumentRepository, PostgresVendorCreditApplicationDocumentRepository>();
        services.AddScoped<IFxRevaluationDocumentRepository, PostgresFxRevaluationDocumentRepository>();
        services.AddScoped<IAccountingReportRepository, PostgresAccountingReportRepository>();
        services.AddScoped<IAccountingDocumentReviewRepository, PostgresAccountingDocumentReviewRepository>();
        services.AddScoped<IJournalEntryReviewRepository, PostgresJournalEntryReviewRepository>();
        services.AddScoped<IReceiptGrIrPostingRepository, PostgresReceiptGrIrPostingRepository>();
        services.AddScoped<ISalesIssueCogsPostingRepository, PostgresSalesIssueCogsPostingRepository>();
        // H1: Expense Void compensation now flows through the Posting Engine via
        // PostExpenseVoidCommandHandler instead of hand-rolled SQL in
        // PostgreSqlExpenseStore. The repo reads the original Expense JE and
        // builds a pre-flipped ExpenseVoidPostingDocument.
        services.AddScoped<IExpenseVoidPostingRepository, PostgresExpenseVoidPostingRepository>();
        services.AddScoped<IInvoiceReversePostingRepository, PostgresInvoiceReversePostingRepository>();
        services.AddScoped<IBillReversePostingRepository, PostgresBillReversePostingRepository>();
        services.AddScoped<IInvoiceDropShipCogsPostingRepository, PostgresInvoiceDropShipCogsPostingRepository>();
        services.AddScoped<IDropShipClearingAgingReader, PostgresDropShipClearingAgingReader>();
        services.AddScoped<IDropShipClearingWriteOffRepository, PostgresDropShipClearingWriteOffRepository>();
        services.AddScoped<IAccountingPeriodRepository, PostgresAccountingPeriodRepository>();
        services.AddScoped<IYearEndPreCloseChecksReader, PostgresYearEndPreCloseChecksReader>();
        services.AddScoped<IAuditLogReader, PostgresAuditLogReader>();
        services.AddScoped<ISalesIssueCogsStatusReader, PostgresSalesIssueCogsStatusReader>();
        services.AddScoped<ICustomerDepositPostingRepository, PostgresCustomerDepositPostingRepository>();
        services.AddScoped<ICustomerDepositApplicationRepository, PostgresCustomerDepositApplicationRepository>();
        services.AddScoped<ICustomerDepositReader, PostgresCustomerDepositReader>();
        services.AddScoped<IReceiptGrIrClearingAccountPolicyRepository, PostgresReceiptGrIrClearingAccountPolicyRepository>();
        services.AddScoped<IReceiptGrIrApSettlementControlStore, PostgresReceiptGrIrApSettlementControlStore>();
        services.AddScoped<IReceiptGrIrSettlementPostingRepository, PostgresReceiptGrIrSettlementPostingRepository>();
        return services;
    }

    public static IServiceCollection AddGeneralLedgerAndFx(this IServiceCollection services)
    {
        services.AddSingleton<JournalEntryNumberLookup, PostgreSqlJournalEntryNumberLookup>();
        services.AddSingleton<GlIJournalEntryLifecycleStore, PostgreSqlJournalEntryLifecycleStore>();
        services.AddSingleton<GlIJournalEntryLifecycleWorkflow, GlJournalEntryLifecycleWorkflow>();
        // Manual-journal save + post wiring. The global::Modules.GL.JournalEntry workflow
        // orchestrates draft persistence, FX snapshot resolution, and the PostingStore
        // hand-off in one call; the API endpoint POST /accounting/manual-journals/save-and-post
        // is the single consumer today.
        services.AddSingleton<global::Engines.FX.FxRateLookup.IFxRateStore, global::Infrastructure.PostgreSQL.FX.PostgreSqlFxRateStore>();
        services.AddSingleton<global::Engines.FX.FxRateLookup.IFxRateSelectionService, global::Engines.FX.FxRateLookup.FxRateSelectionService>();
        services.AddSingleton<global::Modules.GL.JournalEntry.IJournalEntryAccountCatalog, global::Infrastructure.PostgreSQL.GL.PostgreSqlJournalEntryAccountCatalog>();
        services.AddSingleton<global::Modules.GL.JournalEntry.IJournalEntryDraftStore, global::Infrastructure.PostgreSQL.GL.PostgreSqlJournalEntryDraftStore>();
        services.AddSingleton<global::Modules.GL.JournalEntry.IJournalEntryPostingStore, global::Infrastructure.PostgreSQL.GL.PostgreSqlJournalEntryPostingStore>();
        services.AddSingleton<global::Modules.GL.JournalEntry.IJournalEntryWorkflow, global::Modules.GL.JournalEntry.JournalEntryWorkflow>();
        services.AddScoped<IFxSnapshotRepository, PostgresFxSnapshotRepository>();
        services.AddScoped<ICompanyBookPolicyStore, PostgreSqlCompanyBookPolicyStore>();
        services.AddScoped<ICompanyBookPolicyWorkflow, CompanyBookPolicyWorkflow>();
        services.AddScoped<ICompanyCurrencyProvisioningStore, PostgreSqlCompanyCurrencyProvisioningStore>();
        services.AddScoped<ICompanyCurrencyGovernanceWorkflow, CompanyCurrencyGovernanceWorkflow>();
        return services;
    }

    public static IServiceCollection AddCompanyAccessAndPermissions(this IServiceCollection services)
    {
        // Per-company module-flag gate. Singletons because the cache lives
        // across requests; see Citus.SysAdmin.Api/Program.cs for the matching
        // registration that owns the write-side governance UI.
        services.AddSingleton<global::Modules.Company.FeatureManagement.ICompanyModuleFlagStore,
            global::Infrastructure.PostgreSQL.Company.PostgreSqlCompanyModuleFlagStore>();
        services.AddSingleton<global::Modules.Company.FeatureManagement.ICompanyModuleFlagWorkflow,
            global::Modules.Company.FeatureManagement.CompanyModuleFlagWorkflow>();
        // Permission-store binding for the schema bootstrap path. The schema
        // init at startup calls EnsureSchemaAsync on this; the SysAdmin API
        // owns the write-side workflow and has its own registration. Without
        // this, Production-mode startups crash with "No service for type ..."
        // when SchemaManagement:ApplyOnStartup is true.
        services.AddScoped<ICompanyMembershipPermissionStore, PostgreSqlCompanyMembershipPermissionStore>();
        // PR-4A: Tralanz permission model evaluator. Read-only; no consumer
        // endpoints yet (PR-4C wires the first batch). Registered here so
        // host smoke tests + future filters can resolve it from the same DI
        // container as the rest of the CompanyAccess global::Infrastructure.
        services.AddScoped<global::Modules.CompanyAccess.Permissions.IPermissionEvaluator,
            global::Infrastructure.PostgreSQL.CompanyAccess.PostgreSqlPermissionEvaluator>();
        // PR-4E: grant/revoke write path for the new permission model.
        // Workflow validates via IPermissionEvaluator.CanGrantAsync, store
        // writes to company_user_permissions + audit_logs in one transaction.
        services.AddScoped<global::Modules.CompanyAccess.Permissions.IPermissionGrantStore,
            global::Infrastructure.PostgreSQL.CompanyAccess.PostgreSqlPermissionGrantStore>();
        services.AddScoped<global::Modules.CompanyAccess.Permissions.IPermissionGrantWorkflow,
            global::Modules.CompanyAccess.Permissions.PermissionGrantWorkflow>();
        return services;
    }

    public static IServiceCollection AddPricingTasksAndSalesTax(this IServiceCollection services, IConfiguration configuration)
    {
        // Inventory item pricing (Batch 4). The store is stateless beyond
        // the connection factory; the resolver is a thin normalizer. Both
        // singletons.
        services.AddSingleton<IInventoryItemPriceStore,
            global::Infrastructure.PostgreSQL.Inventory.PostgreSqlInventoryItemPriceStore>();
        services.AddSingleton<IItemPriceResolver, ItemPriceResolver>();
        // Tasks module (Batch 5). Singletons — the store is stateless and
        // the workflow only adds state-machine validation.
        services.AddSingleton<ITaskStore, global::Infrastructure.PostgreSQL.Tasks.PostgreSqlTaskStore>();
        services.AddSingleton<ITaskWorkflow, TaskWorkflow>();
        // Sales Tax v2 module (S2.0). Engine is pure compute; the catalog
        // reader implementation in Infrastructure/PostgreSQL/SalesTax/ runs the
        // v2-tables SELECT join (sales_tax_codes ↔ components ↔ as-of rates ↔
        // jurisdictions ↔ box mappings). Persister writes to
        // document_line_sales_tax_snapshots. S2.1 wires the engine + persister
        // into PostgresInvoiceDocumentRepository.SaveDraftAsync, gated by the
        // SalesTaxV2:Enabled flag below (default off → unchanged behaviour).
        services.Configure<SalesTaxV2Options>(
            configuration.GetSection(SalesTaxV2Options.SectionName));
        services.AddSingleton<
            Citus.Modules.SalesTax.Application.Contracts.ISalesTaxCatalogReader,
            global::Infrastructure.PostgreSQL.SalesTax.PostgreSqlSalesTaxCatalogReader>();
        services.AddSingleton<
            Citus.Modules.SalesTax.Application.Contracts.ISalesTaxEngine,
            Citus.Modules.SalesTax.Application.SalesTaxEngine>();
        services.AddSingleton<
            Citus.Modules.SalesTax.Application.Contracts.ITaxSnapshotPersister,
            global::Infrastructure.PostgreSQL.SalesTax.PostgreSqlTaxSnapshotPersister>();
        // Batch 8: AR / AP line-link validator + per-table task_id column
        // initializer. Stateless singletons; the schema initializer runs at
        // startup to add the task_id column to every line table.
        services.AddSingleton<ITaskLineLinkValidator, TaskLineLinkValidator>();
        services.AddSingleton<global::Infrastructure.PostgreSQL.Tasks.PostgresTaskLinkSchemaInitializer>();
        // Batch 9: AR invoice <-> Task billing coordinator. Bookkeeping only --
        // AR's draft/post path stays untouched; callers invoke MarkAsBilledAsync
        // after a successful post and RollbackBillingAsync after a void.
        services.AddSingleton<ITaskBillingCoordinator, TaskBillingCoordinator>();
        // Batch 10: Task operational + billed margin read model. Live SQL
        // aggregation over bill_lines + expense_lines; no materialised view.
        services.AddSingleton<ITaskMarginReportService,
            global::Infrastructure.PostgreSQL.Tasks.PostgreSqlTaskMarginReportService>();
        // TaskDetailPage reverse view: per-task rollup of every linked AR /
        // AP document. Single SQL UNION across the 4 *_lines tables Batch 8
        // stamped with task_id.
        services.AddSingleton<ITaskRelatedDocumentsService,
            global::Infrastructure.PostgreSQL.Tasks.PostgreSqlTaskRelatedDocumentsService>();
        return services;
    }

    public static IServiceCollection AddPostingEngine(this IServiceCollection services)
    {
        services.AddScoped<IArOpenItemRepository, PostgresArOpenItemRepository>();
        services.AddScoped<IApOpenItemRepository, PostgresApOpenItemRepository>();
        services.AddScoped<IOpenItemAdjustmentAccountMappingRepository, PostgresOpenItemAdjustmentAccountMappingRepository>();
        services.AddScoped<ISettlementApplicationRepository, PostgresSettlementApplicationRepository>();
        services.AddScoped<IFxRevaluationApplyRepository, PostgresFxRevaluationApplyRepository>();
        services.AddScoped<IUnitOfWork, PostgresUnitOfWork>();
        services.AddScoped<IPostingValidator, DefaultPostingValidator>();
        services.AddScoped<IPostingPeriodPolicyValidator, PostgresPostingPeriodPolicyValidator>();
        services.AddScoped<ITaxEngine, NullTaxEngine>();
        services.AddScoped<IFxResolutionService, LocalFirstFxResolutionService>();
        services.AddSingleton<IFxRateCacheRepository, PostgresFxRateCacheRepository>();
        services.AddScoped<IRecommendedFxRateService, LocalFirstRecommendedFxRateService>();
        services.AddHttpClient<IFrankfurterFxRateClient, FrankfurterFxRateClient>(client =>
        {
            client.BaseAddress = new Uri(FrankfurterFxRateClient.ProviderBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.Add("User-Agent", "Citus.Accounting.Api/1.0");
        });
        services.AddScoped<IPostingFragmentBuilder, AccountingPostingFragmentBuilder>();
        services.AddScoped<IJournalAggregator, DefaultJournalAggregator>();
        services.AddScoped<IJournalEntryWriter, PostgresJournalEntryWriter>();
        services.AddScoped<IPostingEngine, DefaultPostingEngine>();
        return services;
    }

    public static IServiceCollection AddPostingCommandHandlers(this IServiceCollection services)
    {
        services.AddScoped<PostManualJournalCommandHandler>();
        services.AddScoped<PostInvoiceCommandHandler>();
        services.AddScoped<PostSalesReceiptCommandHandler>();
        services.AddScoped<PostRefundReceiptCommandHandler>();
        services.AddScoped<PostBankTransferCommandHandler>();
        services.AddScoped<PostBankDepositCommandHandler>();
        services.AddScoped<PostTaxReturnCommandHandler>();
        services.AddScoped<PostCreditNoteCommandHandler>();
        services.AddScoped<PostBillCommandHandler>();
        services.AddScoped<PostReceiptWorkflow>();
        services.AddScoped<PostReceiptGrIrCommandHandler>();
        services.AddScoped<PostSalesIssueCogsCommandHandler>();
        // H1: orchestrates the Expense Void compensating JE via the Posting Engine.
        services.AddScoped<PostExpenseVoidCommandHandler>();
        services.AddScoped<PostInvoiceReverseCommandHandler>();
        services.AddScoped<PostBillReverseCommandHandler>();
        // P0-2 (C2): compensating COGS post on invoice-reverse. Mirror of the
        // forward sales-issue COGS handler with isReverse=true on the posting
        // document. Invoked by PostgresAccountingDocumentReviewRepository
        // .CompleteReverseRequestExecutionAsync as part of the invoice-reverse
        // flow, idempotent at the journal-entry source-type probe.
        services.AddScoped<PostSalesIssueCogsReverseCommandHandler>();
        services.AddScoped<PostInvoiceDropShipCogsCommandHandler>();
        services.AddScoped<WriteOffDropShipClearingCommandHandler>();
        services.AddScoped<PostCustomerDepositCommandHandler>();
        services.AddScoped<ApplyCustomerDepositsToInvoiceCommandHandler>();
        services.AddScoped<ExecuteReceiptGrIrSettlementCommandHandler>();
        services.AddScoped<PostReceiptGrIrSettlementJournalCommandHandler>();
        services.AddScoped<ClearReceiptGrIrSettlementOpenItemCommandHandler>();
        services.AddScoped<ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler>();
        services.AddScoped<PostVendorCreditCommandHandler>();
        services.AddScoped<PrepareReceivePaymentDraftCommandHandler>();
        services.AddScoped<PostReceivePaymentCommandHandler>();
        services.AddScoped<PostCreditApplicationCommandHandler>();
        services.AddScoped<PreparePayBillDraftCommandHandler>();
        services.AddScoped<PostPayBillCommandHandler>();
        services.AddScoped<PostVendorCreditApplicationCommandHandler>();
        services.AddScoped<PostArOpenItemAdjustmentCommandHandler>();
        services.AddScoped<PostApOpenItemAdjustmentCommandHandler>();
        services.AddScoped<PrepareFxRevaluationBatchCommandHandler>();
        services.AddScoped<PrepareFxRevaluationUnwindBatchCommandHandler>();
        services.AddScoped<PrepareFxRevaluationCascadeUnwindBatchCommandHandler>();
        services.AddScoped<PostFxRevaluationBatchCommandHandler>();
        services.AddScoped<PostFxRevaluationCascadeUnwindCommandHandler>();
        return services;
    }

    public static IServiceCollection AddSearchCoreMasterDataAndPdf(this IServiceCollection services)
    {
        services.AddSingleton<IBankReconciliationStore, PostgreSqlBankReconciliationStore>();
        services.AddSingleton<Citus.Accounting.Application.Companies.ICompanyMoneyDecimalsStore, global::Infrastructure.PostgreSQL.Companies.PostgreSqlCompanyMoneyDecimalsStore>();
        services.AddSingleton<UnitySearchPolicyRegistry>();
        services.AddSingleton<IUnitySearchProjectionStore, PostgreSqlUnitySearchProjectionStore>();
        services.AddSingleton<IUnitySearchQueryService, PostgreSqlUnitySearchQueryService>();
        services.AddSingleton<IUnitySearchStatsStore, PostgreSqlUnitySearchStatsStore>();
        // Inner engine registered as the concrete type so the unityAI reranking
        // decorator below can take it as a dependency without a self-cycle.
        services.AddSingleton<UnitySearchEngine>();

        // Per-user profile overrides (display name today; future: avatar / locale).
        // Persists across bootstrap-session reloads so name changes stick.
        services.AddSingleton<IUserProfileOverrideStore, PostgreSqlUserProfileOverrideStore>();

        // Tax code catalog (per-company). Reads/writes the existing tax_codes
        // table from the migration draft; safe defaults fill the columns the V1
        // settings UI does not yet expose (recoverability_mode, account refs).
        services.AddSingleton<ITaxCodeStore, PostgreSqlTaxCodeStore>();
        // Sales Tax redesign (R2): Tax Code bundles (tax_code_sets) — read surface
        // for the per-line tax pickers.
        services.AddSingleton<ITaxCodeSetStore, PostgreSqlTaxCodeSetStore>();

        // Chart of Accounts (per-company). Reads/writes the existing accounts
        // table from the migration draft. The UnitySearch projection store
        // (SeedAccountDocumentsAsync) reads the same table on its periodic
        // refresh, so newly-created accounts surface in pickers automatically.
        // is_system rows are protected: update / activate-toggle refuse to
        // modify them so AR/AP/FX control accounts stay stable.
        services.AddSingleton<IAccountStore, PostgreSqlAccountStore>();

        // Per-company Units of Measure (UOM). Seeded by the 2026-05-25 UOM
        // foundation migration + a companies-after-insert trigger. Read-only
        // in V1; the operator-facing CRUD lands when Settings → UOM is added.
        services.AddSingleton<IUomStore, PostgreSqlUomStore>();

        // Customer master data (per-company). Anchors invoices, receive
        // payments, and AR open-item tracking. Entity numbers are
        // auto-generated to match the platform-wide ENYYYYxxxxxxxx contract.
        services.AddSingleton<ICustomerStore, PostgreSqlCustomerStore>();
        // Read-only aggregates for the Customer detail page: financial-summary
        // (open balance + overdue count + unbilled work) and the unified
        // invoice / sales-order / quote transactions timeline. Backed by
        // ar_open_items + invoices/quotes/sales_orders unions.
        services.AddSingleton<ICustomerOverviewQueries, PostgreSqlCustomerOverviewQueries>();
        // First-class shipping address book per customer. Distinct from the
        // historical-address picker (which derives suggestions from past
        // quotes / sales orders); this one is the persisted CRUD surface
        // surfaced on the Customer Profile tab.
        services.AddSingleton<ICustomerShippingAddressBookStore, PostgreSqlCustomerShippingAddressBookStore>();
        // Read-only AP-side aggregates for the Vendor detail page: financial
        // summary (open balance + overdue bill count + open PO count) and the
        // unified bills + POs + vendor-credits transactions timeline.
        services.AddSingleton<IVendorOverviewQueries, PostgreSqlVendorOverviewQueries>();
        // First-class shipping address book per vendor — narrower use case
        // than the customer side (mostly returns / drop-ship origins) but
        // the same shape so the UX stays consistent across counterparties.
        services.AddSingleton<IVendorShippingAddressBookStore, PostgreSqlVendorShippingAddressBookStore>();

        // Read-only company profile lookup (legal_name, address, contacts) for
        // surfaces that print the company on a document — invoice / quote / PO
        // PDFs, email signatures, etc. Read path only; writes go through the
        // SysAdmin First-Company Wizard.
        services.AddSingleton<ICompanyProfileQuery, PostgresCompanyProfileQuery>();

        // Invoice PDF rendering (Batch 1 of the invoice-send / template work).
        // QuestPDF runs CPU-only, no external dependency. Community license is
        // free for any company with annual revenue under USD $1M.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        services.AddSingleton<IInvoicePdfRenderer, QuestPdfInvoiceRenderer>();
        services.AddSingleton<IStatementPdfRenderer, QuestPdfStatementRenderer>();
        return services;
    }

    public static IServiceCollection AddDeliveryAndDocumentStores(this IServiceCollection services)
    {
        // AES-GCM protector for SysAdmin-entered secrets — SMTP password and
        // AI provider API key live in Postgres in encrypted form, decrypted
        // just-in-time by the SMTP / AI senders. Same key as TOTP, distinct
        // envelope prefix.
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformSecretProtector,
            Citus.Platform.Infrastructure.Persistence.PlatformSecretProtector>();

        // SysAdmin-managed AI provider config (provider, base URL, model,
        // encrypted API key). Registered here so the runtime model router and
        // IUnityAiProvider impls in this process can read what an operator
        // configured in the SysAdmin shell. The schema is created/migrated by
        // the SysAdmin API on startup; we just read.
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformAiProviderConfigStore,
            Citus.Platform.Infrastructure.Persistence.PostgresPlatformAiProviderConfigStore>();
        // 30-second cached, decrypt-once view of the AI provider config — keeps
        // the per-call DB roundtrip + AES-GCM unwrap off the search hot path.
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformAiProviderRuntimeResolver,
            Citus.Platform.Infrastructure.Notifications.PlatformAiProviderRuntimeResolver>();

        // Invoice email send (Batch 2). The SMTP sender reuses the platform's
        // PlatformEmailDeliveryOptions so SysAdmin verification mail and
        // Business invoice mail share one outbound configuration. Send-history
        // rows go to a separate append-only ledger so the posting-engine
        // schema stays untouched.
        services.AddSingleton<IInvoiceEmailSender, SmtpInvoiceEmailSender>();
        services.AddSingleton<IInvoiceSendHistoryStore, PostgresInvoiceSendHistoryStore>();

        // Invoice templates (Batch 3). Each company gets three starter templates
        // (Modern / Classic / Minimal) seeded lazily on first access, with the
        // "Modern" preset auto-marked default. Operators customize via
        // Settings -> Invoice templates; the chosen default's branding flows
        // through every PDF download and email send.
        services.AddSingleton<IInvoiceTemplateStore, PostgresInvoiceTemplateStore>();

        // Vendor master data (per-company). AP-side mirror of ICustomerStore;
        // anchors bills, pay-bill settlement, and AP aging.
        services.AddSingleton<IVendorStore, PostgreSqlVendorStore>();

        // Payment terms catalog (per-company). Backs Settings → Payment Terms
        // and the per-vendor Payment Term picker. net_days drives bill due
        // dates downstream; the catalog is intentionally minimal for V1.
        services.AddSingleton<IPaymentTermStore, PostgreSqlPaymentTermStore>();

        // Sales-side pre-billing documents: Quotes (a.k.a. estimates) and the
        // Sales Orders they convert into. Neither hits the GL — they live as
        // informational documents until invoiced through the existing Invoice
        // flow.
        services.AddSingleton<IQuoteStore, PostgreSqlQuoteStore>();
        services.AddSingleton<ISalesOrderStore, PostgreSqlSalesOrderStore>();

        // AP-side: Bill (vendor invoice) draft + lifecycle. The heavy posting
        // pipeline (PostBillCommandHandler, FX snapshot, AP open item) stays
        // in Citus.Accounting.Infrastructure; this store is the document-level
        // CRUD surface for the Bill page. V1 drives status transitions only;
        // the GL writes wire in alongside the PO + Inventory batch.
        services.AddSingleton<IBillStore, PostgreSqlBillStore>();

        // AP-side: Purchase Order document surface. Owns ap_purchase_orders /
        // ap_purchase_order_lines — distinct from the inventory-grade
        // purchase_orders table that the existing posting infrastructure owns.
        // Convergence between the two PO surfaces is a migration item for the
        // Inventory batch.
        services.AddSingleton<IPurchaseOrderStore, PostgreSqlPurchaseOrderStore>();

        // AP-side: Expense (cash outflow) document surface. Owns expenses /
        // expense_lines. Posted-only state machine — Expense reflects payments
        // already made, no Draft. V1 framework writes the document but defers
        // the journal-entry pipeline alongside the Bill GL integration batch.
        // S5.4: factory so the expense store gets the SalesTax engine + flag.
        // SalesTaxV2Options lives in Citus.Accounting.Infrastructure (not
        // global::Infrastructure.PostgreSQL where the store is), so the flag is passed as a
        // plain bool resolved here.
        services.AddSingleton<IExpenseStore>(sp => new PostgreSqlExpenseStore(
            sp.GetRequiredService<global::Infrastructure.PostgreSQL.PostgreSqlConnectionFactory>(),
            sp.GetService<Citus.Modules.SalesTax.Application.Contracts.ISalesTaxEngine>(),
            sp.GetRequiredService<IOptions<SalesTaxV2Options>>().Value.Enabled));

        // CoA starter templates. Static C# data (no DB tables); the seeder is
        // additive — re-applying the same template skips rows that already
        // exist by (company_id, code).
        services.AddSingleton<ICoaTemplateRegistry, StaticCoaTemplateRegistry>();
        services.AddSingleton<ICoaTemplateSeeder, CoaTemplateSeeder>();
        return services;
    }

    public static IServiceCollection AddPlatformSchemaAndProvisioning(this IServiceCollection services)
    {
        // ----- unityAI V1 -------------------------------------------------------
        // Authority: AI_PRODUCT_ARCHITECTURE.md
        // Defaults are conservative: gateway off, AI hints pending, traces sampled.
        services.AddSingleton<UnityAiFeatureFlagAccessor>();
        services.AddSingleton<PostgreSqlUnityAiSchemaInitializer>();

        // Platform schema setup. Runs unconditionally in every environment because
        // the Accounting API's accounts / tax codes / journal entries / FX writes
        // FK into currency_catalog, companies, users, company_memberships, and
        // these tables (plus the ISO 4217 currency rows) must exist before any
        // business write. PostgresPlatformFirstCompanyProvisioningRepository
        // guarantees idempotency via IF NOT EXISTS / ON CONFLICT DO NOTHING.
        services.AddSingleton<Citus.Platform.Core.Runtime.SysAdminPasswordHasher>();
        services.AddSingleton<Citus.Platform.Infrastructure.Persistence.PostgresPlatformFirstCompanyProvisioningRepository>();
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformFirstCompanyProvisioningRepository>(static sp =>
            sp.GetRequiredService<Citus.Platform.Infrastructure.Persistence.PostgresPlatformFirstCompanyProvisioningRepository>());
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformAdditionalCompanyProvisioningRepository>(static sp =>
            sp.GetRequiredService<Citus.Platform.Infrastructure.Persistence.PostgresPlatformFirstCompanyProvisioningRepository>());
        services.AddSingleton<PlatformSchemaInitializer>();
        return services;
    }

    public static IServiceCollection AddBusinessAuthAndNotifications(this IServiceCollection services)
    {
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
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformSmtpConfigStore,
            Citus.Platform.Infrastructure.Persistence.PostgresPlatformSmtpConfigStore>();
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformEmailDeliveryConfigResolver,
            Citus.Platform.Infrastructure.Notifications.PlatformEmailDeliveryConfigResolver>();
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformVerificationNotificationSender,
            Citus.Platform.Infrastructure.Notifications.SmtpPlatformVerificationNotificationSender>();
        // Brute-force lockout: 5 fails in 15 min → 15-min temporary lock,
        // 3 temp locks in 36 h → permanent lock. Must be registered BEFORE
        // the business session repo so the latter's constructor injection
        // resolves it.
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformLoginLockoutPolicy,
            Citus.Platform.Infrastructure.Persistence.PostgresPlatformLoginLockoutPolicy>();
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository,
            Citus.Platform.Infrastructure.Persistence.PostgresPlatformBusinessSessionRepository>();
        // Self-serve forgot-password flow for the Business shell. SysAdmin
        // shell uses a different (manual-grant) reset path; not wired here.
        services.AddSingleton<Citus.Platform.Core.Abstractions.IPlatformBusinessPasswordResetService,
            Citus.Platform.Infrastructure.Persistence.PostgresPlatformBusinessPasswordResetService>();
        return services;
    }

    public static IServiceCollection AddSearchLearningDashboardAndAi(this IServiceCollection services)
    {
        services.AddSingleton<IAiJobRunStore, PostgreSqlAiJobRunStore>();
        services.AddSingleton<IAiRequestLogStore, PostgreSqlAiRequestLogStore>();
        services.AddSingleton<IUnitysearchEventStore, PostgreSqlUnitysearchEventStore>();
        services.AddSingleton<IUnitysearchUsageStatStore, PostgreSqlUnitysearchUsageStatStore>();
        services.AddSingleton<IUnitysearchPairStatStore, PostgreSqlUnitysearchPairStatStore>();
        services.AddSingleton<IUnitysearchRecentQueryStore, PostgreSqlUnitysearchRecentQueryStore>();
        services.AddSingleton<IUnitysearchRankingHintStore, PostgreSqlUnitysearchRankingHintStore>();
        services.AddSingleton<
            Citus.Modules.UnitySearch.Application.Contracts.IUnitySearchQueryClassPriorStore,
            global::Infrastructure.PostgreSQL.UnitySearch.PostgreSqlUnitySearchQueryClassPriorStore>();
        services.AddSingleton<IUnitysearchDecisionTraceStore, PostgreSqlUnitysearchDecisionTraceStore>();
        services.AddSingleton<IUnitysearchRankingEngine, UnitysearchRankingEngine>();
        // Register the reranking decorator as the IUnitySearchEngine the rest of
        // the API resolves. It wraps the concrete UnitySearchEngine and falls
        // through to its ordering when the learning flag is off or when the
        // ranking engine throws — search must never break because of unityAI.
        services.AddSingleton<IUnitySearchEngine>(sp => new UnitysearchAiRerankingEngine(
            inner: sp.GetRequiredService<UnitySearchEngine>(),
            ranking: sp.GetRequiredService<IUnitysearchRankingEngine>(),
            flags: sp.GetRequiredService<UnityAiFeatureFlagAccessor>(),
            logger: sp.GetRequiredService<ILogger<UnitysearchAiRerankingEngine>>()));
        services.AddSingleton<IReportUsageEventStore, PostgreSqlReportUsageEventStore>();
        services.AddSingleton<IReportUsageStatStore, PostgreSqlReportUsageStatStore>();
        services.AddSingleton<IDashboardUserWidgetStore, PostgreSqlDashboardUserWidgetStore>();
        services.AddSingleton<IDashboardWidgetSuggestionStore, PostgreSqlDashboardWidgetSuggestionStore>();
        services.AddSingleton<IDashboardSuggestionService, DashboardSuggestionService>();
        services.AddSingleton<IActionCenterTaskStore, PostgreSqlActionCenterTaskStore>();
        services.AddSingleton<IActionCenterTaskEventStore, PostgreSqlActionCenterTaskEventStore>();
        services.AddSingleton<IActionCenterTaskService, ActionCenterTaskService>();
        // Real OpenAI-compatible adapter — works for OpenAI, BigModel/智谱,
        // DeepSeek, Together, OpenRouter, LM Studio, and any other backend that
        // implements POST /chat/completions. The Noop provider stays registered
        // as a last-resort lookup if some future router selects "noop" by name.
        // The gateway picks providers by name, so multiple registrations are
        // harmless and the typed HttpClient lives behind IHttpClientFactory.
        services.AddHttpClient(nameof(OpenAiCompatibleAiProvider));
        services.AddSingleton<IUnityAiProvider, OpenAiCompatibleAiProvider>();
        services.AddSingleton<IUnityAiProvider, NoopAiProvider>();
        // Replaces NoopUnityAiModelRouter — reads SysAdmin AI provider config
        // to decide which provider/model name to select. Returns null (= gateway
        // short-circuits to Disabled) when no row is configured or the API key
        // is empty, so the deterministic ranking path stays intact.
        services.AddSingleton<IUnityAiModelRouter, PlatformConfigBackedModelRouter>();
        // Real prompt registry seeded with task templates (currently:
        // unitysearch.rerank.v1 for the hint-distillation flow). Tasks not
        // registered here cause the gateway to short-circuit at gate 3 — that's
        // the intended fail-closed behavior.
        services.AddSingleton<IUnityAiPromptRegistry, UnityAiPromptRegistry>();
        // Hint distillation orchestrator: reads top-clicked entities for a
        // company, asks the gateway for boost suggestions, persists into
        // unitysearch_ranking_hints. Manual trigger lives at
        // /internal/ai/distill-unitysearch?companyId=<uuid>.
        services.AddSingleton<IUnitysearchHintDistillationService, UnitysearchHintDistillationService>();
        // Plan B: per-company query-intent cache. Read path sits on every
        // search call (single index lookup); write path is off-band via the
        // backfill service + Task.Run enqueuer.
        services.AddSingleton<IUnitysearchQueryIntentCacheStore,
            global::Infrastructure.PostgreSQL.UnitySearch.PostgreSqlUnitysearchQueryIntentCacheStore>();
        services.AddSingleton<IUnitysearchQueryIntentBackfillService,
            UnitysearchQueryIntentBackfillService>();
        services.AddSingleton<IUnitysearchQueryIntentBackfillEnqueuer,
            UnitysearchQueryIntentBackfillEnqueuer>();
        // Plan C-Population: pgvector embedding provider + per-company
        // doc-embedding back-fill. Embedding provider mirrors the chat-completion
        // adapter (same IPlatformAiProviderRuntimeResolver, same OpenAI-compatible
        // URL pattern, just /v1/embeddings instead of /v1/chat/completions).
        // All three gated independently on UNITYAI_EMBEDDINGS_ENABLED so the
        // operator can opt into vector recall without enabling the rest of the
        // AI gateway, or vice versa.
        services.AddSingleton<IUnityAiEmbeddingProvider,
            OpenAiCompatibleEmbeddingProvider>();
        services.AddSingleton<ISearchDocumentEmbeddingStore,
            global::Infrastructure.PostgreSQL.UnitySearch.PostgreSqlSearchDocumentEmbeddingStore>();
        services.AddSingleton<ISearchDocumentEmbeddingBackfillService,
            SearchDocumentEmbeddingBackfillService>();
        // Real shape validator. Catches malformed provider responses before
        // the gateway tries to deserialize into TOutput, so a runaway LLM
        // can't poison the structured-output deserialization path.
        services.AddSingleton<IUnityAiStructuredOutputValidator, UnityAiStructuredOutputValidator>();
        services.AddSingleton<IUnityAiGateway, UnityAiGateway>();
        services.AddSingleton<IAccountingCopilotPlanner, NoopAccountingCopilotPlanner>();
        return services;
    }

    public static IServiceCollection AddActionCenterTaskProviders(this IServiceCollection services)
    {
        // V1 ships only the system-setup task provider as a real rule, plus null
        // providers for AR / AP / banking / sales-tax — those domains do not yet
        // expose the read shape these rules need, and the architecture forbids
        // fabricating tasks. Each null provider logs once-per-call so the gap
        // is operationally visible.
        services.AddSingleton<IActionCenterTaskProvider>(sp => new SystemSetupActionCenterTaskProvider(
            readSnapshotAsync: (companyId, ct) =>
            {
                // V1: optimistic snapshot — assume profile complete and SMTP configured
                // until a real settings reader is wired in. Wiring this to the real
                // company_settings table is the first follow-up.
                return ValueTask.FromResult(new SystemSetupSnapshot(SmtpConfigured: true, CompanyProfileComplete: true));
            },
            sp.GetRequiredService<ILogger<SystemSetupActionCenterTaskProvider>>()));
        services.AddSingleton<IActionCenterTaskProvider>(sp => new NullActionCenterTaskProvider(
            name: "ar_overdue_invoices",
            missingDomain: "AR open-invoice aggregate not exposed",
            sp.GetRequiredService<ILogger<NullActionCenterTaskProvider>>()));
        services.AddSingleton<IActionCenterTaskProvider>(sp => new NullActionCenterTaskProvider(
            name: "ap_bills_due_soon",
            missingDomain: "AP unpaid-bill aggregate not exposed",
            sp.GetRequiredService<ILogger<NullActionCenterTaskProvider>>()));
        services.AddSingleton<IActionCenterTaskProvider>(sp => new NullActionCenterTaskProvider(
            name: "bank_unmatched_transactions",
            missingDomain: "bank reconciliation task aggregate not yet exposed",
            sp.GetRequiredService<ILogger<NullActionCenterTaskProvider>>()));
        services.AddSingleton<IActionCenterTaskProvider>(sp => new NullActionCenterTaskProvider(
            name: "sales_tax_filing_due",
            missingDomain: "sales-tax filing calendar not yet exposed",
            sp.GetRequiredService<ILogger<NullActionCenterTaskProvider>>()));
        return services;
    }
}
