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

namespace Citus.Accounting.Api.Initialization;

/// <summary>
/// Startup runtime-DDL bootstrap extracted verbatim from Program.cs (P4).
/// Provisions per-store schema at boot ONLY when SchemaManagement:ApplyOnStartup
/// is enabled (default: Development); production leaves it disabled and applies
/// migrations externally. The EnsureSchemaAsync ordering and its FK / 42P01 /
/// companies-first guards are preserved exactly as written.
/// </summary>
public static class AccountingSchemaBootstrapper
{
    public static async Task ApplyIfEnabledAsync(WebApplication app)
    {
        if (ShouldApplyRuntimeSchemaManagement(app.Configuration, app.Environment))
        {
            app.Logger.LogWarning(
                "Runtime schema management is enabled for Citus.Accounting.Api. Production deployments should run migrations externally and leave this disabled.");

            await using (var startupScope = app.Services.CreateAsyncScope())
            {
                var runtimeStateRepository = startupScope.ServiceProvider.GetRequiredService<IPlatformRuntimeStateRepository>();
                var accountingPeriodRepository = startupScope.ServiceProvider.GetRequiredService<IAccountingPeriodRepository>();
                var receiptDocumentRepository = startupScope.ServiceProvider.GetRequiredService<IReceiptDocumentRepository>();
                var purchaseOrderDocumentRepository = startupScope.ServiceProvider.GetRequiredService<IPurchaseOrderDocumentRepository>();
                var billReceiptMatchingRepository = startupScope.ServiceProvider.GetRequiredService<IBillReceiptMatchingRepository>();
                var inventoryFoundationStore = startupScope.ServiceProvider.GetRequiredService<IInventoryFoundationStore>();
                var inventoryReceiptStore = startupScope.ServiceProvider.GetRequiredService<IInventoryReceiptStore>();
                var inventoryIssueStore = startupScope.ServiceProvider.GetRequiredService<IInventoryIssueStore>();
                var inventoryShipmentStore = startupScope.ServiceProvider.GetRequiredService<IInventoryShipmentStore>();
                var receiptInventoryActivationStore = startupScope.ServiceProvider.GetRequiredService<IReceiptInventoryActivationStore>();
                var receiptInventoryValuationStore = startupScope.ServiceProvider.GetRequiredService<IReceiptInventoryValuationStore>();
                var receiptInventoryCostLayerEmissionStore = startupScope.ServiceProvider.GetRequiredService<IReceiptInventoryCostLayerEmissionStore>();
                var receiptGrIrBridgeStore = startupScope.ServiceProvider.GetRequiredService<IReceiptGrIrBridgeStore>();
                var receiptGrIrPostingRepository = startupScope.ServiceProvider.GetRequiredService<IReceiptGrIrPostingRepository>();
                var receiptGrIrSettlementStore = startupScope.ServiceProvider.GetRequiredService<IReceiptGrIrApSettlementControlStore>();
                var receiptGrIrSettlementPostingRepository = startupScope.ServiceProvider.GetRequiredService<IReceiptGrIrSettlementPostingRepository>();
                var adjustmentAccountMappingRepository = startupScope.ServiceProvider.GetRequiredService<IOpenItemAdjustmentAccountMappingRepository>();
                var unitySearchProjectionStore = startupScope.ServiceProvider.GetRequiredService<IUnitySearchProjectionStore>();
                var unityAiSchemaInitializer = startupScope.ServiceProvider.GetRequiredService<PostgreSqlUnityAiSchemaInitializer>();
                var userProfileOverrideStore = startupScope.ServiceProvider.GetRequiredService<IUserProfileOverrideStore>();
                var taxCodeStore = startupScope.ServiceProvider.GetRequiredService<ITaxCodeStore>();
                var accountStore = startupScope.ServiceProvider.GetRequiredService<IAccountStore>();
                var customerStore = startupScope.ServiceProvider.GetRequiredService<ICustomerStore>();
                var customerShippingAddressBookStore = startupScope.ServiceProvider.GetRequiredService<ICustomerShippingAddressBookStore>();
                var vendorShippingAddressBookStore = startupScope.ServiceProvider.GetRequiredService<IVendorShippingAddressBookStore>();
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
                var customerDepositSchema = startupScope.ServiceProvider.GetRequiredService<PostgresCustomerDepositSchemaBootstrap>();
                var v1WriteFlowSchema = startupScope.ServiceProvider.GetRequiredService<PostgresV1WriteFlowSchemaBootstrap>();
                var companyModuleFlagStore = startupScope.ServiceProvider.GetRequiredService<ICompanyModuleFlagStore>();
                var companyMembershipPermissionStoreSchema = startupScope.ServiceProvider.GetRequiredService<global::Modules.CompanyAccess.Memberships.ICompanyMembershipPermissionStore>();
                var inventoryItemPriceStore = startupScope.ServiceProvider.GetRequiredService<IInventoryItemPriceStore>();
                var taskStore = startupScope.ServiceProvider.GetRequiredService<ITaskStore>();
                var taskLinkSchema = startupScope.ServiceProvider.GetRequiredService<global::Infrastructure.PostgreSQL.Tasks.PostgresTaskLinkSchemaInitializer>();
                await runtimeStateRepository.EnsureSchemaAsync(CancellationToken.None);
                // Platform tables (currency_catalog, companies, users, company_memberships,
                // company_books, etc.) must exist FIRST because the master entity tables
                // below (customers, vendors, accounts) and v1WriteFlow all FK to companies.
                // On a fresh DB the earlier ordering blew up with 42P01: relation "companies"
                // does not exist.
                await platformSchema.EnsureAsync(CancellationToken.None);
                // Master entities: must exist before v1WriteFlow which FKs / indexes them.
                await taxCodeStore.EnsureSchemaAsync(CancellationToken.None);
                await accountStore.EnsureSchemaAsync(CancellationToken.None);
                await customerStore.EnsureSchemaAsync(CancellationToken.None);
                await customerShippingAddressBookStore.EnsureSchemaAsync(CancellationToken.None);
                await vendorStore.EnsureSchemaAsync(CancellationToken.None);
                await vendorShippingAddressBookStore.EnsureSchemaAsync(CancellationToken.None);
                await paymentTermStore.EnsureSchemaAsync(CancellationToken.None);
                // fxRateCache creates `company_fx_rate_snapshots` which v1WriteFlow indexes.
                await fxRateCache.EnsureSchemaAsync(CancellationToken.None);
                // Transactional tables in v1WriteFlow (invoices, bills, receive_payments,
                // credit_notes, etc.) FK back to customers / vendors / accounts. Then
                // customerDepositSchema ALTERs receive_payments to add extra_deposit_amount.
                await v1WriteFlowSchema.EnsureSchemaAsync(CancellationToken.None);
                await customerDepositSchema.EnsureSchemaAsync(CancellationToken.None);
                await accountingPeriodRepository.EnsureSchemaAsync(CancellationToken.None);
                await receiptDocumentRepository.EnsureSchemaAsync(CancellationToken.None);
                await purchaseOrderDocumentRepository.EnsureSchemaAsync(CancellationToken.None);
                await billReceiptMatchingRepository.EnsureSchemaAsync(CancellationToken.None);
                await inventoryFoundationStore.EnsureSchemaAsync(CancellationToken.None);
                await inventoryReceiptStore.EnsureSchemaAsync(CancellationToken.None);
                await inventoryIssueStore.EnsureSchemaAsync(CancellationToken.None);
                await inventoryShipmentStore.EnsureSchemaAsync(CancellationToken.None);
                await receiptInventoryActivationStore.EnsureSchemaAsync(CancellationToken.None);
                await receiptInventoryValuationStore.EnsureSchemaAsync(CancellationToken.None);
                await receiptInventoryCostLayerEmissionStore.EnsureSchemaAsync(CancellationToken.None);
                await receiptGrIrBridgeStore.EnsureSchemaAsync(CancellationToken.None);
                await receiptGrIrPostingRepository.EnsureSchemaAsync(CancellationToken.None);
                await receiptGrIrSettlementStore.EnsureSchemaAsync(CancellationToken.None);
                await receiptGrIrSettlementPostingRepository.EnsureSchemaAsync(CancellationToken.None);
                await adjustmentAccountMappingRepository.EnsureSchemaAsync(CancellationToken.None);
                await unitySearchProjectionStore.EnsureSchemaAsync(CancellationToken.None);
                await unityAiSchemaInitializer.EnsureSchemaAsync(CancellationToken.None);
                await userProfileOverrideStore.EnsureSchemaAsync(CancellationToken.None);
                await quoteStore.EnsureSchemaAsync(CancellationToken.None);
                await salesOrderStore.EnsureSchemaAsync(CancellationToken.None);
                await billStore.EnsureSchemaAsync(CancellationToken.None);
                await purchaseOrderStore.EnsureSchemaAsync(CancellationToken.None);
                await expenseStore.EnsureSchemaAsync(CancellationToken.None);
                await invoiceSendHistoryStore.EnsureSchemaAsync(CancellationToken.None);
                await invoiceTemplateStore.EnsureSchemaAsync(CancellationToken.None);
                await smtpConfigStore.EnsureSchemaAsync(CancellationToken.None);
                // business_sessions / mfa_challenges / mfa_enrollments tables need
                // to exist before /auth/login can issue tokens or fetch user records.
                await businessSessionRepository.EnsureSchemaAsync(CancellationToken.None);
                await businessPasswordResetService.EnsureSchemaAsync(CancellationToken.None);
                await loginLockoutPolicy.EnsureSchemaAsync(CancellationToken.None);
                // company_module_flags is a small, stateless table; safe to bootstrap
                // last. The SysAdmin API also runs this same bootstrap, so first-to-boot
                // wins — both calls are no-ops on subsequent boots.
                await companyModuleFlagStore.EnsureSchemaAsync(CancellationToken.None);
                // One-time expansion of legacy coarse permission tokens into
                // their fine-grained equivalents. Whichever API boots first
                // performs the rewrite; both calls are then no-ops.
                await companyMembershipPermissionStoreSchema.EnsureSchemaAsync(CancellationToken.None);
                await inventoryItemPriceStore.EnsureSchemaAsync(CancellationToken.None);
                await taskStore.EnsureSchemaAsync(CancellationToken.None);
                // Batch 8: must come after every AR/AP line-table bootstrap
                // above so the ALTER lands on tables that already exist. The
                // initializer uses ALTER TABLE IF EXISTS so missing parents
                // are silently skipped — re-runs on next boot pick them up.
                await taskLinkSchema.EnsureSchemaAsync(CancellationToken.None);
            }
        }
        else
        {
            app.Logger.LogInformation(
                "Runtime schema management is disabled for Citus.Accounting.Api; database migrations must be applied before startup.");
        }
    }

    private static bool ShouldApplyRuntimeSchemaManagement(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration.GetValue<bool?>("SchemaManagement:ApplyOnStartup")
            ?? configuration.GetValue<bool?>("Tralanz:SchemaManagement:ApplyOnStartup");

        return configured ?? environment.IsDevelopment();
    }
}
