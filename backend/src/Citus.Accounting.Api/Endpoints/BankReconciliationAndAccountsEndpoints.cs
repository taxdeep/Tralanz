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

namespace Citus.Accounting.Api.Endpoints;

/// <summary>
/// BankReconciliationAndAccounts endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class BankReconciliationAndAccountsEndpoints
{
    public static void MapBankReconciliationAndAccountsEndpoints(this RouteGroupBuilder accounting)
    {

        // ===========================================================================
        // Bank Reconciliation
        //
        // This surface reconciles posted ledger_entries for one bank, cash, or
        // credit-card statement account.
        // Completion is backend-owned: the store re-loads selected ledger rows inside
        // a serializable transaction, locks them, rejects non-zero differences, then
        // inserts immutable reconciliation header + line snapshots.
        // ===========================================================================

        accounting.MapGet(
            "/reconciliation/ledger",
            async (
                Guid accountId,
                DateOnly? statementDate,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (accountId == Guid.Empty) return Results.BadRequest(new { message = "Statement account is required." });
                var authorityBlock = RequireBankReconciliationAuthority(session, "view");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var rows = await store.ListUnreconciledLedgerEntriesAsync(
                        session.ActiveCompanyId,
                        accountId,
                        statementDate ?? DateOnly.FromDateTime(DateTime.Today),
                        cancellationToken);
                    return Results.Ok(new BankReconciliationLedgerResponse(rows));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        accounting.MapPost(
            "/reconciliation/complete",
            async (
                BankReconciliationCompleteHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "complete");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                var validation = ValidateBankReconciliationRequest(request);
                if (validation is not null)
                {
                    return Results.BadRequest(new { message = validation });
                }

                try
                {
                    var summary = await store.CompleteAsync(
                        session.ActiveCompanyId,
                        session.UserId,
                        new BankReconciliationCompleteInput(
                            request.BankAccountId,
                            request.StatementDate,
                            request.OpeningBalance,
                            request.EndingBalance,
                            request.LedgerEntryIds,
                            request.Notes),
                        cancellationToken);
                    return Results.Ok(summary);
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
                {
                    return Results.Conflict(new
                    {
                        message = "This statement or one of its ledger entries has already been reconciled.",
                        constraint = ex.ConstraintName
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        // ===========================================================================
        // R-1: Bank reconciliation DRAFT lifecycle endpoints.
        //
        // See BANKING_RECONCILE_PLAN.md Sections 7 (state machine) and 10 (API
        // surface). Each endpoint is gated by RequireBankReconciliationAuthority,
        // matching the existing /reconciliation/ledger + /reconciliation/complete
        // endpoints. The legacy two endpoints above remain functional so the
        // current /reconciliation page keeps working until R-3 replaces it.
        // ===========================================================================

        accounting.MapPost(
            "/reconciliation/draft",
            async (
                BankReconciliationDraftOpenHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "open_draft");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                var validation = ValidateBankReconciliationDraftOpen(request);
                if (validation is not null)
                {
                    return Results.BadRequest(new { message = validation });
                }

                try
                {
                    var draft = await store.OpenDraftAsync(
                        session.ActiveCompanyId,
                        session.UserId,
                        new BankReconciliationDraftOpenInput(
                            request.BankAccountId,
                            request.StatementDate,
                            request.OpeningBalance,
                            request.EndingBalance,
                            request.Notes),
                        cancellationToken);
                    return Results.Ok(draft);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        accounting.MapGet(
            "/reconciliation/draft",
            async (
                Guid? accountId,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "view");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                if (accountId is null || accountId == Guid.Empty)
                {
                    return Results.BadRequest(new { message = "accountId query parameter is required." });
                }

                var draft = await store.FindOpenDraftForAccountAsync(
                    session.ActiveCompanyId,
                    accountId.Value,
                    cancellationToken);
                return draft is null ? Results.NoContent() : Results.Ok(draft);
            });

        accounting.MapGet(
            "/reconciliation/draft/{draftId:guid}",
            async (
                Guid draftId,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "view");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                var draft = await store.LoadDraftAsync(session.ActiveCompanyId, draftId, cancellationToken);
                return draft is null
                    ? Results.NotFound(new { message = $"Reconciliation draft '{draftId:D}' was not found." })
                    : Results.Ok(draft);
            });

        accounting.MapGet(
            "/reconciliation/draft/{draftId:guid}/candidates",
            async (
                Guid draftId,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "view");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var candidates = await store.ListDraftCandidatesAsync(
                        session.ActiveCompanyId,
                        draftId,
                        cancellationToken);
                    return Results.Ok(new BankReconciliationDraftCandidatesResponse(draftId, candidates));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.NotFound(new { message = ex.Message });
                }
            });

        accounting.MapPut(
            "/reconciliation/draft/{draftId:guid}/cleared",
            async (
                Guid draftId,
                BankReconciliationDraftToggleHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "edit_draft");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                if (request.LedgerEntryId == Guid.Empty)
                {
                    return Results.BadRequest(new { message = "ledgerEntryId is required." });
                }

                try
                {
                    var draft = await store.ToggleLineAsync(
                        session.ActiveCompanyId,
                        draftId,
                        request.LedgerEntryId,
                        request.Cleared,
                        cancellationToken);
                    return Results.Ok(draft);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        accounting.MapMethods(
            "/reconciliation/draft/{draftId:guid}",
            new[] { HttpMethods.Patch },
            async (
                Guid draftId,
                BankReconciliationDraftPatchHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "edit_draft");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var draft = await store.PatchStatementInfoAsync(
                        session.ActiveCompanyId,
                        draftId,
                        new BankReconciliationDraftPatchInput(
                            request.OpeningBalance,
                            request.EndingBalance,
                            request.StatementDate,
                            request.Notes),
                        cancellationToken);
                    return Results.Ok(draft);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        accounting.MapDelete(
            "/reconciliation/draft/{draftId:guid}",
            async (
                Guid draftId,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "abandon_draft");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    await store.AbandonDraftAsync(session.ActiveCompanyId, session.UserId, draftId, cancellationToken);
                    return Results.NoContent();
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        accounting.MapPost(
            "/reconciliation/draft/{draftId:guid}/complete",
            async (
                Guid draftId,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "complete");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var summary = await store.CompleteDraftAsync(
                        session.ActiveCompanyId,
                        session.UserId,
                        draftId,
                        cancellationToken);
                    return Results.Ok(summary);
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
                {
                    return Results.Conflict(new
                    {
                        message = "This statement or one of its ledger entries has already been reconciled.",
                        constraint = ex.ConstraintName
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        accounting.MapPost(
            "/reconciliation/{reconciliationId:guid}/undo",
            async (
                Guid reconciliationId,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "undo");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    await store.UndoCompletedAsync(
                        session.ActiveCompanyId,
                        session.UserId,
                        reconciliationId,
                        cancellationToken);
                    return Results.NoContent();
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        // ===========================================================================
        // R-4: carry-forward + report endpoints.
        //
        //   GET /reconciliation/last-completed?accountId=...
        //     Returns the most-recent completed reconciliation summary used to
        //     prefill the beginning balance + statement date on the next Start
        //     form. 204 when the account has never been reconciled.
        //
        //   GET /reconciliation/{id}
        //     Full report payload for a completed (or undone-and-abandoned)
        //     reconciliation: header + frozen line snapshot. Drives the
        //     /banking/reconciliation/{id}/report page added in R-3+R-4.
        // ===========================================================================

        accounting.MapGet(
            "/reconciliation/last-completed",
            async (
                Guid? accountId,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "view");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }
                if (accountId is null || accountId == Guid.Empty)
                {
                    return Results.BadRequest(new { message = "accountId query parameter is required." });
                }

                var summary = await store.GetLastCompletedAsync(
                    session.ActiveCompanyId,
                    accountId.Value,
                    cancellationToken);
                return summary is null ? Results.NoContent() : Results.Ok(summary);
            });

        accounting.MapGet(
            "/reconciliation/{reconciliationId:guid}",
            async (
                Guid reconciliationId,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "view");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                var report = await store.LoadReconciliationReportAsync(
                    session.ActiveCompanyId,
                    reconciliationId,
                    cancellationToken);
                return report is null
                    ? Results.NotFound(new { message = $"Reconciliation '{reconciliationId:D}' was not found or is still in progress." })
                    : Results.Ok(report);
            });

        // ===========================================================================
        // R-2: Bank Register endpoint. Read-only view of every posted ledger entry
        // on a bank / cash / credit-card account, with cleared/uncleared status and
        // (when cleared) a pointer back to the reconciliation that locked it. Used
        // by the Bank Register Blazor page in /banking/register/{accountId}.
        //
        // Pagination: simple LIMIT @take in V1. Cursor pagination + virtual scroll
        // land in R-5 along with the rest of the perf hardening.
        // ===========================================================================

        accounting.MapGet(
            "/bank-register/{accountId:guid}",
            async (
                Guid accountId,
                int? take,
                BusinessSessionContextAccessor sessionAccessor,
                IBankReconciliationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var authorityBlock = RequireBankReconciliationAuthority(session, "view");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }
                if (accountId == Guid.Empty)
                {
                    return Results.BadRequest(new { message = "Bank account id is required." });
                }

                try
                {
                    var entries = await store.ListBankRegisterAsync(
                        session.ActiveCompanyId,
                        accountId,
                        take ?? 200,
                        cancellationToken);
                    return Results.Ok(new BankRegisterResponse(accountId, entries));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                            IsActive: request.IsActive ?? true,
                            ParentAccountId: request.ParentAccountId), // Batch C
                        cancellationToken);
                    // H15: refresh the topbar / account picker projection so the
                    // new account shows up immediately.
                    await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlAccountEdit);

        accounting.MapPut(
            "/accounts/{id:guid}",
            async (
                Guid id,
                AccountUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountStore store,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                            IsActive: request.IsActive ?? true,
                            ParentAccountId: request.ParentAccountId), // Batch C
                        cancellationToken);
                    if (updated is not null)
                    {
                        // H15: keep topbar / account picker in sync with the rename
                        // / re-categorization.
                        await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
                    }
                    // Update returns null when the row is missing OR when is_system
                    // blocked the WHERE clause. The maintenance UI hides edit on
                    // system rows so a 404 here is the right honest response.
                    return updated is null
                        ? Results.NotFound(new { message = "Account not found, or it is a system control account that cannot be edited from this surface." })
                        : Results.Ok(updated);
                }
                catch (InvalidOperationException ex)
                {
                    // Batch D: lock predicate ("Account X is locked. Unlock it ...").
                    return Results.BadRequest(new { message = ex.Message });
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlAccountEdit);

        // Batch D: lock / unlock toggle. Uses the existing GlAccountEdit
        // permission — no separate "Lock" permission for V1 (consistent
        // with QBO; an operator with edit rights can choose to lock).
        accounting.MapPost(
            "/accounts/{id:guid}/lock",
            async (
                Guid id,
                AccountLockHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var updated = await store.SetLockAsync(
                    session.ActiveCompanyId,
                    id,
                    new AccountLockInput(request.Lock, session.UserId),
                    cancellationToken);
                return updated is null
                    ? Results.NotFound(new { message = "Account not found or system-protected." })
                    : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlAccountEdit);

        accounting.MapPost(
            "/accounts/{id:guid}/activate",
            async (
                Guid id,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, true, cancellationToken);
                return updated is null
                    ? Results.NotFound(new { message = "Account not found or system-protected." })
                    : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlAccountEdit);

        accounting.MapPost(
            "/accounts/{id:guid}/deactivate",
            async (
                Guid id,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, false, cancellationToken);
                return updated is null
                    ? Results.NotFound(new { message = "Account not found or system-protected." })
                    : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlAccountEdit);

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlAccountEdit);
    }
}
