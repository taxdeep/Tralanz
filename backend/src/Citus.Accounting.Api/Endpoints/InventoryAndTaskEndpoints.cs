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
/// InventoryAndTask endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class InventoryAndTaskEndpoints
{
    public static void MapInventoryAndTaskEndpoints(this RouteGroupBuilder accounting)
    {

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

        // -----------------------------------------------------------------------
        // Inventory Module activation (M2 of the Inventory V1 plan).
        // One POST handles the wizard's submit:
        //   1. Re-runs the canonical CoA seeder so older companies pick up the
        //      M1 standard accounts (idempotent additive).
        //   2. Sets default costing method via SavePolicyAsync.
        //   3. Creates the single "Main Warehouse" via SaveWarehouseAsync.
        //   4. Stamps the companies-table activation flags via the dedicated
        //      activation store.
        // Step 5 of the original plan (per-item opening balance) is deferred —
        // no OpeningBalanceReceipt helper exists yet. Wizard surfaces a link
        // to the existing Inventory Adjustment workbench for opening stock.
        // -----------------------------------------------------------------------
        accounting.MapPost(
            "/inventory/activate",
            async (
                InventoryActivationHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryModuleActivationStore activationStore,
                IInventoryFoundationStore foundationStore,
                ICoaTemplateSeeder coaSeeder,
                ICompanyProfileQuery companyProfileQuery,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    // Look up the company's chosen account_code_length so the
                    // additive seeder can scale canonical 5-digit template codes
                    // to the same width as the operator's curated chart. Without
                    // this a 4-digit chart would end up with an orphan 5-digit
                    // row whenever a system_role is missing.
                    var companyProfile = await companyProfileQuery.GetByIdAsync(
                        session.ActiveCompanyId, cancellationToken);

                    // Step 1 — make sure every standard CoA account exists
                    // (covers companies that already onboarded before M1). Runs
                    // in additive mode: the company almost certainly already has
                    // a curated chart by the time it reaches this wizard, so the
                    // strict empty-chart guard would block activation. Per-row
                    // idempotency in SeedSystemAccountAsync skips rows that
                    // already exist; only the missing inventory-side rows get
                    // inserted.
                    var coaSummary = await coaSeeder.SeedAsync(
                        session.ActiveCompanyId,
                        "ca_general_small_business",
                        cancellationToken,
                        additive: true,
                        accountCodeLength: companyProfile?.AccountCodeLength);

                    // Step 2 — costing method (locks on first inventory document).
                    var costingMethod = InventoryActivationRequestParser.ParseCostingMethod(request.CostingMethod);
                    await foundationStore.SavePolicyAsync(
                        new InventoryCostingPolicyUpdateRequest(
                            CompanyId: session.ActiveCompanyId,
                            UserId: session.UserId,
                            DefaultCostingMethod: costingMethod,
                            NegativeStockAllowed: false,
                            RequireWriteOffApproval: true),
                        cancellationToken);

                    // Step 3 — default warehouse. SaveWarehouseAsync's INSERT path
                    // hits the (company_id, lower(warehouse_code)) unique index
                    // when re-running the wizard, so look up the existing MAIN
                    // first and pass its id to take the UPDATE branch. Without
                    // this the activation route is single-shot — exactly the
                    // opposite of what the wizard's "Re-apply settings" copy
                    // promises operators.
                    var warehouseName = string.IsNullOrWhiteSpace(request.WarehouseName)
                        ? "Main Warehouse"
                        : request.WarehouseName.Trim();
                    var existingWarehouses = await foundationStore.ListWarehousesAsync(
                        session.ActiveCompanyId, includeInactive: true, cancellationToken);
                    var existingMain = existingWarehouses.FirstOrDefault(w =>
                        string.Equals(w.WarehouseCode, "MAIN", StringComparison.OrdinalIgnoreCase));
                    var warehouseId = await foundationStore.SaveWarehouseAsync(
                        new InventoryWarehouseUpsertRequest(
                            CompanyId: session.ActiveCompanyId,
                            UserId: session.UserId,
                            WarehouseId: existingMain?.Id,
                            WarehouseCode: "MAIN",
                            Name: warehouseName,
                            Description: "Default warehouse created by the Inventory activation wizard."),
                        cancellationToken);

                    // Step 4 — flip the companies-table flags. Done last so the
                    // module only appears active if every preceding step worked.
                    var profileTag = (request.ProfileTag ?? string.Empty).Trim();
                    var activated = await activationStore.MarkEnabledAsync(
                        session.ActiveCompanyId, profileTag, cancellationToken);

                    return Results.Ok(new InventoryActivationHttpResponse(
                        ModuleEnabled: activated.ModuleEnabled,
                        EnabledAt: activated.EnabledAt,
                        LockedAt: activated.LockedAt,
                        ProfileTag: activated.ProfileTag,
                        DefaultCostingMethod: InventoryActivationRequestParser.FormatCostingMethod(costingMethod),
                        WarehouseId: warehouseId,
                        WarehouseCode: "MAIN",
                        WarehouseName: warehouseName,
                        CoaAccountsCreated: coaSummary.CreatedCount,
                        CoaAccountsAlreadyPresent: coaSummary.SkippedCount));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsModulesToggle);

        // -----------------------------------------------------------------------
        // Warehouses — list / rename for the Inventory tier's Warehouses page.
        // V1 inventory is single-warehouse so this list is short, but the
        // shape is multi-row-ready for the ERP tier.
        // -----------------------------------------------------------------------
        accounting.MapGet(
            "/warehouses",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryFoundationStore store,
                bool? includeInactive,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await store.ListWarehousesAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
                return Results.Ok(rows);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryWarehouseView);

        accounting.MapPut(
            "/warehouses/{warehouseId:guid}",
            async (
                Guid warehouseId,
                WarehouseRenameHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryFoundationStore store,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();
                if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { message = "Name is required." });

                try
                {
                    await store.SaveWarehouseAsync(
                        new InventoryWarehouseUpsertRequest(
                            CompanyId: session.ActiveCompanyId,
                            UserId: session.UserId,
                            WarehouseId: warehouseId,
                            WarehouseCode: (request.WarehouseCode ?? string.Empty).Trim().ToUpperInvariant(),
                            Name: request.Name.Trim(),
                            Description: string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()),
                        cancellationToken);
                    // H15: refresh warehouse picker projection on rename / upsert.
                    await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
                    return Results.Ok(new { warehouseId });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryWarehouseEdit);

        accounting.MapGet(
            "/inventory/activation-state",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryModuleActivationStore activationStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var state = await activationStore.GetStateAsync(session.ActiveCompanyId, cancellationToken);
                if (state is null) return Results.NotFound();
                return Results.Ok(new
                {
                    state.ModuleEnabled,
                    state.EnabledAt,
                    state.LockedAt,
                    state.ProfileTag
                });
            });

        accounting.MapGet(
            "/items",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryFoundationStore store,
                bool? includeInactive,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await store.ListItemsAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
                return Results.Ok(rows.Select(MapItemSummary));
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryItemView);

        accounting.MapPost(
            "/items",
            async (
                InventoryItemUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryFoundationStore store,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                var validation = ValidateItemRequest(request);
                if (validation is not null) return Results.BadRequest(new { message = validation });

                try
                {
                    var itemId = await store.SaveItemAsync(
                        BuildItemUpsertRequest(session.ActiveCompanyId, session.UserId, itemId: null, request),
                        cancellationToken);

                    // H15: refresh topbar / item / stock picker projection so the
                    // new item shows up immediately.
                    await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);

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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryItemCreate);

        accounting.MapPut(
            "/items/{itemId:guid}",
            async (
                Guid itemId,
                InventoryItemUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryFoundationStore store,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                var validation = ValidateItemRequest(request);
                if (validation is not null) return Results.BadRequest(new { message = validation });

                try
                {
                    await store.SaveItemAsync(
                        BuildItemUpsertRequest(session.ActiveCompanyId, session.UserId, itemId, request),
                        cancellationToken);

                    // H15: keep topbar / item / stock picker in sync with the
                    // rename / re-categorization.
                    await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);

                    var rows = await store.ListItemsAsync(session.ActiveCompanyId, includeInactive: true, cancellationToken);
                    var saved = rows.FirstOrDefault(r => r.Id == itemId);
                    return saved is null ? Results.NoContent() : Results.Ok(MapItemSummary(saved));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryItemEdit);

        accounting.MapPost(
            "/items/{itemId:guid}/activate",
            async (
                Guid itemId,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryFoundationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                try
                {
                    await store.SetItemActiveAsync(session.ActiveCompanyId, itemId, isActive: true, cancellationToken);
                    return Results.NoContent();
                }
                catch (InvalidOperationException ex)
                {
                    return Results.NotFound(new { message = ex.Message });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryItemEdit);

        accounting.MapPost(
            "/items/{itemId:guid}/deactivate",
            async (
                Guid itemId,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryFoundationStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                try
                {
                    await store.SetItemActiveAsync(session.ActiveCompanyId, itemId, isActive: false, cancellationToken);
                    return Results.NoContent();
                }
                catch (InvalidOperationException ex)
                {
                    return Results.NotFound(new { message = ex.Message });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryItemEdit);

        // ---------------------------------------------------------------------------
        // Inventory item pricing (Batch 4).
        //
        // CRUD + resolve over `inventory_item_prices`. Read paths require
        // `inventory.price.view`; write paths require `inventory.price.edit`.
        // First real consumer of the [HasPermission] decorator pattern
        // introduced in Batch 3 — owners get implicit access via the
        // session-load Union (Batch 3.6).
        //
        // Resolution semantics (see InventoryItemPriceQuery + the SQL in the
        // store): customer-specific > price-list-specific > highest matching
        // quantity tier > most recent effective_from. Caller passes
        // document-date as `asOf` so resolution is deterministic over time.
        // ---------------------------------------------------------------------------
        accounting.MapGet(
            "/items/{itemId:guid}/prices",
            async (
                Guid itemId,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryItemPriceStore priceStore,
                bool? includeInactive,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await priceStore.ListAsync(
                    session.ActiveCompanyId,
                    itemId,
                    includeInactive ?? false,
                    cancellationToken);
                return Results.Ok(rows);
            }).RequirePermission(CompanyMembershipPermissionCatalog.InventoryPriceView);

        accounting.MapPost(
            "/items/{itemId:guid}/prices",
            async (
                Guid itemId,
                InventoryItemPriceUpsertRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryItemPriceStore priceStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                try
                {
                    // Force create — ignore any Id the caller put on the body
                    // so POST is unambiguously "new row". Updates use PUT.
                    var saved = await priceStore.UpsertAsync(
                        session.ActiveCompanyId,
                        itemId,
                        request with { Id = null },
                        cancellationToken);
                    return Results.Ok(saved);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }).RequirePermission(CompanyMembershipPermissionCatalog.InventoryPriceEdit);

        accounting.MapPut(
            "/items/{itemId:guid}/prices/{priceId:guid}",
            async (
                Guid itemId,
                Guid priceId,
                InventoryItemPriceUpsertRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryItemPriceStore priceStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                try
                {
                    var saved = await priceStore.UpsertAsync(
                        session.ActiveCompanyId,
                        itemId,
                        request with { Id = priceId },
                        cancellationToken);
                    return Results.Ok(saved);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }).RequirePermission(CompanyMembershipPermissionCatalog.InventoryPriceEdit);

        accounting.MapDelete(
            "/items/{itemId:guid}/prices/{priceId:guid}",
            async (
                Guid itemId,
                Guid priceId,
                BusinessSessionContextAccessor sessionAccessor,
                IInventoryItemPriceStore priceStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var deleted = await priceStore.SoftDeleteAsync(
                    session.ActiveCompanyId,
                    priceId,
                    cancellationToken);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(CompanyMembershipPermissionCatalog.InventoryPriceEdit);

        accounting.MapGet(
            "/items/{itemId:guid}/price/resolve",
            async (
                Guid itemId,
                string? currency,
                DateOnly? asOf,
                Guid? customerId,
                string? priceListCode,
                decimal? quantity,
                BusinessSessionContextAccessor sessionAccessor,
                IItemPriceResolver resolver,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                if (string.IsNullOrWhiteSpace(currency))
                {
                    return Results.BadRequest(new { message = "currency is required." });
                }

                try
                {
                    var resolution = await resolver.ResolveAsync(
                        new InventoryItemPriceQuery
                        {
                            CompanyId = session.ActiveCompanyId,
                            ItemId = itemId,
                            CurrencyCode = currency,
                            AsOf = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow),
                            CustomerId = customerId,
                            PriceListCode = priceListCode,
                            Quantity = quantity ?? 1m,
                        },
                        cancellationToken);

                    // Null = no matching price. Callers fall back to their
                    // own default (e.g. inventory_item.unit_price, manual
                    // entry). 404 keeps the contract honest: a "missing
                    // price" is not an error, it's just absence.
                    return resolution is null ? Results.NotFound() : Results.Ok(resolution);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }).RequirePermission(CompanyMembershipPermissionCatalog.InventoryPriceView);

        // ---------------------------------------------------------------------------
        // Tasks (Batch 5).
        //
        // Service-delivery execution units, per Tralance Task Module Authority
        // Summary. State machine: open → completed → billed (Batch 9 wires
        // the AR → bill transition); open or completed → canceled. Edits are
        // accepted only in open. Caller without `task.view.all` only sees the
        // rows assigned to themselves; owner Union (Batch 3.6) grants the
        // full-view permission automatically.
        //
        // Every endpoint is double-gated: RequireModuleEnabled("task")
        // returns 404 if the company hasn't switched the module on, then
        // RequirePermission(...) returns 403 on permission shortfall. The
        // company-isolation guard already applies via the existing route
        // guard pipeline.
        // ---------------------------------------------------------------------------
        accounting.MapGet(
            "/tasks",
            async (
                TaskStatus? status,
                Guid? customerId,
                int? take,
                int? skip,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                // If the caller can't see-all, narrow the SQL to "assigned to
                // me" before it ever hits the DB — defence-in-depth on top of
                // any UI filtering. Owners always pass via the catalog Union.
                var canSeeAll = session.Roles.Contains(CompanyMembershipPermissionCatalog.TaskViewAll, StringComparer.Ordinal);
                var query = new TaskQuery
                {
                    CompanyId = session.ActiveCompanyId,
                    Status = status,
                    CustomerId = customerId,
                    OnlyAssignedToUserId = canSeeAll ? null : session.UserId,
                    Take = take ?? 50,
                    Skip = skip ?? 0,
                };

                var rows = await workflow.ListAsync(query, cancellationToken);
                return Results.Ok(rows);
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskView);

        accounting.MapGet(
            "/tasks/{taskId:guid}",
            async (
                Guid taskId,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var record = await workflow.GetAsync(session.ActiveCompanyId, taskId, cancellationToken);
                if (record is null) return Results.NotFound();

                // Visibility check: if the caller lacks `task.view.all`, they
                // can only see a task that is assigned to them. Same gate as
                // the list endpoint — applied here too because direct-URL
                // access bypasses the list narrowing. Unassigned tasks are
                // hidden from narrow-scope viewers as well (no implicit
                // "everyone can see unowned tasks").
                var canSeeAll = session.Roles.Contains(CompanyMembershipPermissionCatalog.TaskViewAll, StringComparer.Ordinal);
                if (!canSeeAll && record.AssignedToUserId != session.UserId)
                {
                    return Results.NotFound();
                }

                return Results.Ok(record);
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskView);

        // Batch display-label resolver for task ids. Used by the bill /
        // expense / credit-memo edit pages so the per-line TaskPicker can
        // render "TSK-000123 -- Title" instead of a short-GUID placeholder
        // when the page first loads with persisted task_ids. Repeated query
        // parameter binding: ?ids=guid1&ids=guid2&...
        accounting.MapGet(
            "/tasks/lookup",
            async (
                Guid[]? ids,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await store.LookupDisplayAsync(
                    session.ActiveCompanyId,
                    ids ?? Array.Empty<Guid>(),
                    cancellationToken);
                return Results.Ok(rows);
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskView);

        // Per-task reverse rollup of every linked AR / AP document. Same
        // double-gate (module + permission) as the rest of the surface.
        // Read-only; the page just displays what the line tables already
        // contain (no extra state).
        accounting.MapGet(
            "/tasks/{taskId:guid}/related-documents",
            async (
                Guid taskId,
                string? baseCurrency,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskWorkflow workflow,
                ITaskRelatedDocumentsService service,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                // Re-run the same visibility gate the GET /tasks/{id} endpoint
                // applies. Without this an operator without task.view.all could
                // browse the related-docs list of a task they aren't assigned
                // to by guessing its id.
                var task = await workflow.GetAsync(session.ActiveCompanyId, taskId, cancellationToken);
                if (task is null) return Results.NotFound();

                var canSeeAll = session.Roles.Contains(CompanyMembershipPermissionCatalog.TaskViewAll, StringComparer.Ordinal);
                if (!canSeeAll && task.AssignedToUserId != session.UserId)
                {
                    return Results.NotFound();
                }

                // Resolve base currency for FX conversion of each related doc's
                // task_amount. Client passes its ShellState value; default to
                // the task's own currency if absent so the report stays
                // numerically honest (each row converts at rate 1 within its
                // own currency) for legacy callers.
                var resolvedBase = string.IsNullOrWhiteSpace(baseCurrency)
                    ? task.CurrencyCode
                    : baseCurrency.Trim().ToUpperInvariant();
                if (resolvedBase.Length != 3)
                {
                    return Results.BadRequest(new { message = $"baseCurrency must be a 3-letter ISO code; got '{baseCurrency}'." });
                }

                var rows = await service.ListForTaskAsync(session.ActiveCompanyId, taskId, resolvedBase, cancellationToken);
                return Results.Ok(rows);
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskView);

        accounting.MapPost(
            "/tasks",
            async (
                TaskCreateRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    var created = await workflow.CreateAsync(session.ActiveCompanyId, session.UserId, request, cancellationToken);
                    return Results.Created($"/accounting/tasks/{created.Id:D}", created);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskCreate);

        accounting.MapPut(
            "/tasks/{taskId:guid}",
            async (
                Guid taskId,
                TaskUpdateRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    var updated = await workflow.UpdateAsync(session.ActiveCompanyId, taskId, session.UserId, request, cancellationToken);
                    return Results.Ok(updated);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskEdit);

        accounting.MapPost(
            "/tasks/{taskId:guid}/lines",
            async (
                Guid taskId,
                TaskLineUpsertRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    var updated = await workflow.AddLineAsync(session.ActiveCompanyId, taskId, session.UserId, request, cancellationToken);
                    return Results.Ok(updated);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskEdit);

        accounting.MapDelete(
            "/tasks/{taskId:guid}/lines/{lineId:guid}",
            async (
                Guid taskId,
                Guid lineId,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    var updated = await workflow.RemoveLineAsync(session.ActiveCompanyId, taskId, lineId, session.UserId, cancellationToken);
                    return Results.Ok(updated);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskEdit);

        accounting.MapPost(
            "/tasks/{taskId:guid}/complete",
            async (
                Guid taskId,
                TaskStateChangeRequest? request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    var updated = await workflow.CompleteAsync(session.ActiveCompanyId, taskId, session.UserId, request?.Reason, cancellationToken);
                    return Results.Ok(updated);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskComplete);

        accounting.MapPost(
            "/tasks/{taskId:guid}/cancel",
            async (
                Guid taskId,
                TaskStateChangeRequest? request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    var updated = await workflow.CancelAsync(session.ActiveCompanyId, taskId, session.UserId, request?.Reason, cancellationToken);
                    return Results.Ok(updated);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskCancel);

        // Batch 9: Task <-> AR invoice billing bookkeeping. These two
        // endpoints are the only canonical way to flip a task into / out of
        // the `Billed` terminal state. They are invoked by the AR post-success
        // path (mark) and the AR void path (rollback). Neither endpoint
        // touches AR documents -- they exist purely so the Task module knows
        // which tasks are currently locked behind an invoice. Same double-gate
        // pattern as the rest of the surface.
        accounting.MapPost(
            "/tasks/billing/mark",
            async (
                TaskMarkBilledRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskBillingCoordinator coordinator,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    var result = await coordinator.MarkAsBilledAsync(
                        session.ActiveCompanyId,
                        request.InvoiceId,
                        request.CustomerId,
                        request.TaskIds,
                        session.UserId,
                        cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskBill);

        accounting.MapPost(
            "/tasks/billing/rollback",
            async (
                TaskRollbackBillingRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskBillingCoordinator coordinator,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    var result = await coordinator.RollbackBillingAsync(
                        session.ActiveCompanyId,
                        request.InvoiceId,
                        session.UserId,
                        request.Reason,
                        cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskBill);

        // Batch 10: Task gross-margin report. One endpoint, two modes:
        //   mode=operational (default) -> open/completed/billed tasks, filtered by service_date
        //   mode=billed                -> billed-only, filtered by billed_at
        // Filters (from/to/customerId/assigneeId/take/skip) are all optional.
        // The summary in the response always reflects the unpaged filtered set,
        // not just the visible page.
        accounting.MapGet(
            "/tasks/reports/margin",
            async (
                string? mode,
                DateOnly? from,
                DateOnly? to,
                Guid? customerId,
                string? assigneeId,
                string? baseCurrency,
                int? take,
                int? skip,
                BusinessSessionContextAccessor sessionAccessor,
                ITaskMarginReportService reportService,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                TaskMarginReportMode parsedMode;
                switch ((mode ?? "operational").Trim().ToLowerInvariant())
                {
                    case "operational":
                        parsedMode = TaskMarginReportMode.Operational;
                        break;
                    case "billed":
                        parsedMode = TaskMarginReportMode.Billed;
                        break;
                    default:
                        return Results.BadRequest(new { message = $"Unknown report mode '{mode}'. Use 'operational' or 'billed'." });
                }

                // Base currency targets the FX conversion. Client passes its
                // ShellState.ActiveCompany.BaseCurrencyCode; if absent (legacy
                // callers) default to USD so the SQL has a valid pair to query.
                var resolvedBase = string.IsNullOrWhiteSpace(baseCurrency)
                    ? "USD"
                    : baseCurrency.Trim().ToUpperInvariant();
                if (resolvedBase.Length != 3)
                {
                    return Results.BadRequest(new { message = $"baseCurrency must be a 3-letter ISO code; got '{baseCurrency}'." });
                }

                UserId? parsedAssignee = string.IsNullOrWhiteSpace(assigneeId) ? null : UserId.Parse(assigneeId.Trim());

                var query = new TaskMarginReportQuery
                {
                    CompanyId = session.ActiveCompanyId,
                    BaseCurrencyCode = resolvedBase,
                    Mode = parsedMode,
                    FromDate = from,
                    ToDate = to,
                    CustomerId = customerId,
                    AssignedToUserId = parsedAssignee,
                    Take = take ?? 200,
                    Skip = skip ?? 0,
                };

                var result = await reportService.GetReportAsync(query, cancellationToken);
                return Results.Ok(result);
            })
            .RequireModuleEnabled(CompanyModuleFlagCatalog.Task)
            .RequirePermission(CompanyMembershipPermissionCatalog.TaskReportMargin);
    }
}
