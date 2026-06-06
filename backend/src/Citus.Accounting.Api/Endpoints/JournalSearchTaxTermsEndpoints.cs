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
/// JournalSearchTaxTerms endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class JournalSearchTaxTermsEndpoints
{
    public static void MapJournalSearchTaxTermsEndpoints(this RouteGroupBuilder accounting)
    {

        accounting.MapGet(
            "/journal-entries",
            async (
                [AsParameters] JournalEntryListLookupQuery query,
                IJournalEntryReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var items = await repository.ListRecentAsync(
                    query.CompanyId,
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
                    query.CompanyId,
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
                    query.CompanyId,
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

        // Preview-only peek at the next journal display number for the active
        // company. Used by the New Journal Entry form so the user sees the number
        // the system will assign on save (and can override it inline). PEEK only —
        // no reservation here; the actual number is reserved at post time so the
        // preview is best-effort across concurrent operators.
        accounting.MapGet(
            "/journal-entries/next-number",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                global::Engines.Numbering.JournalEntry.IJournalEntryNumberLookup lookup,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
                {
                    return Results.Unauthorized();
                }

                var displayNumber = await lookup.GetNextDisplayNumberAsync(
                    session.ActiveCompanyId,
                    cancellationToken);
                return Results.Ok(new { displayNumber });
            });

        // Void a posted journal entry. Lifecycle workflow doesn't delete the
        // original — it marks the JE as voided and inserts a compensating
        // reverse-side entry so the audit trail stays intact. Returned payload
        // names both the original and the compensation so the UI can deep-link
        // to either after the operation.
        accounting.MapPost(
            "/journal-entries/{journalEntryId:guid}/void",
            async (
                Guid journalEntryId,
                BusinessSessionContextAccessor sessionAccessor,
                global::Modules.GL.JournalEntry.IJournalEntryLifecycleWorkflow lifecycleWorkflow,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value) || string.IsNullOrEmpty(session.UserId.Value))
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var result = await lifecycleWorkflow.VoidAsync(
                        session.ActiveCompanyId,
                        journalEntryId,
                        session.UserId,
                        cancellationToken);
                    // H15-b: JE status flipped posted → voided. Projection's
                    // is_voided / status filter must refresh so the voided JE
                    // disappears from topbar search results.
                    await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
                    return Results.Ok(new
                    {
                        originalJournalEntryId = result.OriginalJournalEntryId,
                        originalDisplayNumber = result.OriginalDisplayNumber,
                        originalStatus = result.OriginalStatus,
                        lifecycleAt = result.LifecycleAt,
                        compensationJournalEntryId = result.CompensationJournalEntryId,
                        compensationDisplayNumber = result.CompensationDisplayNumber,
                        compensationSourceType = result.CompensationSourceType,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlJournalVoid);

        accounting.MapGet(
            "/unity-search",
            async (
                [AsParameters] UnitySearchHttpQuery query,
                BusinessSessionContextAccessor sessionAccessor,
                IUnitySearchEngine engine,
                CancellationToken cancellationToken) =>
            {
                // Permission tokens flow from the current business session into
                // the query so the SQL gate can hide rows whose
                // required_permissions[] don't overlap with what the caller
                // actually holds. Callers without a session (rare — most paths
                // require auth) get an empty list, which means they see only
                // static / public rows.
                var permissions = sessionAccessor.Current?.Roles ?? Array.Empty<string>();

                var result = await engine.SearchAsync(
                    new UnitySearchQuery
                    {
                        CompanyId = query.CompanyId,
                        UserId = query.UserId ?? sessionAccessor.Current?.UserId,
                        Context = string.IsNullOrWhiteSpace(query.Context) ? Citus.Modules.UnitySearch.Domain.Shared.SearchScopeContext.GlobalTopbar : query.Context.Trim(),
                        SearchText = query.Query ?? string.Empty,
                        Take = query.Take ?? 10,
                        Permissions = permissions
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
                if (!userId.HasValue || string.IsNullOrEmpty(userId.Value.Value))
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
                if (!userId.HasValue || string.IsNullOrEmpty(userId.Value.Value))
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
                var userId = !string.IsNullOrEmpty(request.UserId.Value)
                    ? (UserId?)request.UserId
                    : sessionAccessor.Current?.UserId;
                if (!userId.HasValue || string.IsNullOrEmpty(userId.Value.Value))
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
                if (session is null || string.IsNullOrEmpty(session.UserId.Value))
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
                if (session is null || string.IsNullOrEmpty(session.UserId.Value))
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
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

        // Sales Tax redesign (R2 slice 1a): list the user's Tax Codes (bundles of
        // Rules) for the per-line tax pickers. Mirrors GET /tax-codes' shape +
        // applies_to filter.
        accounting.MapGet(
            "/tax-code-sets",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                ITaxCodeSetStore store,
                bool? includeInactive,
                string? appliesTo,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
                {
                    return Results.Unauthorized();
                }

                var rows = await store.ListAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
                if (!string.IsNullOrWhiteSpace(appliesTo))
                {
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
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
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
                            RegistrationNumber: request.RegistrationNumber,
                            IsActive: request.IsActive ?? true,
                            RecoverabilityMode: string.Equals(request.RecoverabilityMode, "none", StringComparison.OrdinalIgnoreCase) ? "none" : "full",
                            PayableAccountId: request.PayableAccountId,
                            RecoverableAccountId: request.RecoverableAccountId),
                        cancellationToken);
                    // H15: refresh tax-code picker projection so the new code shows
                    // up in line-level pickers immediately.
                    await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
                    return Results.Ok(record);
                }
                catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
                {
                    // Unique violation — most likely (company_id, code) clash.
                    return Results.BadRequest(new { message = $"Tax code '{request.Code}' already exists for this company." });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsTaxEdit);

        accounting.MapPut(
            "/tax-codes/{id:guid}",
            async (
                Guid id,
                TaxCodeUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaxCodeStore store,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
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
                            RegistrationNumber: request.RegistrationNumber,
                            IsActive: request.IsActive ?? true,
                            RecoverabilityMode: string.Equals(request.RecoverabilityMode, "none", StringComparison.OrdinalIgnoreCase) ? "none" : "full",
                            PayableAccountId: request.PayableAccountId,
                            RecoverableAccountId: request.RecoverableAccountId),
                        cancellationToken);
                    if (updated is not null)
                    {
                        // H15: keep tax-code picker in sync with the rate / display
                        // name update.
                        await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
                    }
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }
                catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
                {
                    return Results.BadRequest(new { message = $"Tax code '{request.Code}' already exists for this company." });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsTaxEdit);

        accounting.MapPost(
            "/tax-codes/{id:guid}/activate",
            async (
                Guid id,
                BusinessSessionContextAccessor sessionAccessor,
                ITaxCodeStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, true, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsTaxEdit);

        accounting.MapPost(
            "/tax-codes/{id:guid}/deactivate",
            async (
                Guid id,
                BusinessSessionContextAccessor sessionAccessor,
                ITaxCodeStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, false, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsTaxEdit);

        // Sales Tax redesign (R2 slice 2a): Tax Code (bundle) create / edit /
        // activate. Mirrors the /tax-codes mutation surface; the store writes the
        // tax_code_sets row + its tax_code_set_rules membership transactionally.
        accounting.MapPost(
            "/tax-code-sets",
            async (
                TaxCodeSetUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaxCodeSetStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
                {
                    return Results.Unauthorized();
                }
                var validation = ValidateTaxCodeSetInput(request);
                if (validation is not null)
                {
                    return Results.BadRequest(new { message = validation });
                }
                try
                {
                    var record = await store.CreateAsync(session.ActiveCompanyId, MapTaxCodeSetInput(request), cancellationToken);
                    return Results.Ok(record);
                }
                catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
                {
                    return Results.BadRequest(new { message = $"Tax code '{request.Code}' already exists for this company." });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsTaxEdit);

        accounting.MapPut(
            "/tax-code-sets/{id:guid}",
            async (
                Guid id,
                TaxCodeSetUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaxCodeSetStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
                {
                    return Results.Unauthorized();
                }
                var validation = ValidateTaxCodeSetInput(request);
                if (validation is not null)
                {
                    return Results.BadRequest(new { message = validation });
                }
                try
                {
                    var updated = await store.UpdateAsync(session.ActiveCompanyId, id, MapTaxCodeSetInput(request), cancellationToken);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }
                catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
                {
                    return Results.BadRequest(new { message = $"Tax code '{request.Code}' already exists for this company." });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsTaxEdit);

        accounting.MapPost(
            "/tax-code-sets/{id:guid}/activate",
            async (Guid id, BusinessSessionContextAccessor sessionAccessor, ITaxCodeSetStore store, CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, true, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsTaxEdit);

        accounting.MapPost(
            "/tax-code-sets/{id:guid}/deactivate",
            async (Guid id, BusinessSessionContextAccessor sessionAccessor, ITaxCodeSetStore store, CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, false, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsTaxEdit);

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var updated = await store.SetActiveAsync(session.ActiveCompanyId, id, false, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            });
    }
}
