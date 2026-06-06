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
/// CompanyMasterData endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class CompanyMasterDataEndpoints
{
    public static void MapCompanyMasterDataEndpoints(this RouteGroupBuilder accounting)
    {

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var profile = await workflow.GetProfileAsync(session.ActiveCompanyId, cancellationToken);
                return Results.Ok(MapCurrencyProfile(profile));
            });

        // -----------------------------------------------------------------------
        // Per-company Units of Measure (UOM). Read-only in V1 — the 2026-05-25
        // foundation migration seeds 8 defaults and a companies-after-insert
        // trigger seeds them for every new company. Drives the Item edit UOM
        // picker and the qty input step on Task / Invoice / Bill line grids.
        // -----------------------------------------------------------------------
        accounting.MapGet(
            "/uom",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IUomStore store,
                bool? includeInactive,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await store.ListAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
                return Results.Ok(rows.Select(r => new UomHttpSummary(
                    r.Id, r.CompanyId, r.Code, r.Name, r.DecimalPrecision, r.Category, r.IsActive, r.CreatedAt, r.UpdatedAt)));
            });

        // -----------------------------------------------------------------------
        // Per-company module-flag list for the active company. Returns the full
        // catalog merged with persisted state (Enabled=false when the company
        // has never been switched on). Consumed by the Blazor shell to decide
        // which menus to render and by the Task module pages.
        // -----------------------------------------------------------------------
        accounting.MapGet(
            "/company/module-flags",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                ICompanyModuleFlagWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var flags = await workflow.ListAsync(session.ActiveCompanyId, cancellationToken);
                return Results.Ok(flags);
            });

        // -----------------------------------------------------------------------
        // Business-side module-flag toggle. Owners (and anyone holding the
        // settings.modules.toggle token) can self-serve enable/disable a
        // catalog module for the active company. Same persistence + cache
        // invalidation as the SysAdmin path — only the audit row's actor_type
        // differs ('user' vs 'sysadmin') so governance review can distinguish
        // the two pathways.
        //
        // Why this exists alongside the SysAdmin write surface: previously the
        // SysAdmin was the only operator who could flip module-flags. That
        // meant a small-business Owner who wanted Task tracking had to file a
        // ticket. With self-serve toggling the Owner can enable per-company
        // optional modules from Settings → Modules without SysAdmin
        // intervention.
        // -----------------------------------------------------------------------
        accounting.MapPut(
            "/company/module-flags/{moduleKey}",
            async (
                string moduleKey,
                CompanyModuleFlagToggleHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ICompanyModuleFlagWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null
                    || string.IsNullOrEmpty(session.ActiveCompanyId.Value)
                    || string.IsNullOrEmpty(session.UserId.Value))
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var result = await workflow.SetEnabledFromOwnerAsync(
                        session.ActiveCompanyId,
                        moduleKey,
                        request.Enabled,
                        request.Reason ?? string.Empty,
                        session.UserId,
                        cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsModulesToggle);

        // -----------------------------------------------------------------------
        // Create an additional company (Business shell, "+ New Company").
        //
        // The signed-in user (resolved from BusinessSession) becomes the owner
        // of the new company. The caller already has at least one active
        // company — that's how they reached this endpoint — but the active
        // company on the session header is incidental here; this endpoint
        // writes a new row to `companies`, a new `company_memberships` for the
        // caller, and seeds the canonical chart of accounts. After the call
        // returns the Blazor shell switches to the new company by re-fetching
        // /accounting/session/context.
        // -----------------------------------------------------------------------
        accounting.MapPost(
            "/companies",
            async (
                CreateAdditionalCompanyHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                Citus.Platform.Core.Abstractions.IPlatformAdditionalCompanyProvisioningRepository provisioning,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.UserId.Value))
                {
                    return Results.Unauthorized();
                }

                var command = new Citus.Platform.Core.Runtime.PlatformAdditionalCompanyProvisioningCommand
                {
                    OwnerUserId = session.UserId,
                    CompanyName = request.CompanyName ?? string.Empty,
                    EntityType = request.EntityType ?? string.Empty,
                    Industry = request.Industry ?? string.Empty,
                    IncorporatedOn = request.IncorporatedOn,
                    FiscalYearEnd = request.FiscalYearEnd ?? string.Empty,
                    Country = request.Country ?? string.Empty,
                    BaseCurrencyCode = request.BaseCurrencyCode ?? string.Empty,
                    AccountCodeLength = request.AccountCodeLength ?? 5,
                    BusinessNumber = request.BusinessNumber ?? string.Empty,
                    Phone = request.Phone ?? string.Empty,
                    CompanyEmail = request.CompanyEmail ?? string.Empty,
                    AddressLine = request.AddressLine ?? string.Empty,
                    City = request.City ?? string.Empty,
                    ProvinceState = request.ProvinceState ?? string.Empty,
                    PostalCode = request.PostalCode ?? string.Empty,
                    TemplateKey = request.TemplateKey ?? string.Empty
                };

                var result = await provisioning.ProvisionAsync(command, cancellationToken);
                if (!result.Succeeded)
                {
                    return Results.BadRequest(new
                    {
                        code = result.FailureCode,
                        message = result.FailureMessage
                    });
                }

                return Results.Ok(new
                {
                    companyId = result.CompanyId.Value,
                    entityNumber = result.CompanyEntityNumber,
                    companyName = result.CompanyName,
                    baseCurrencyCode = result.BaseCurrencyCode,
                    accountCodeLength = result.AccountCodeLength,
                    templateKey = result.TemplateKey,
                    templateVersion = result.TemplateVersion,
                    starterAccountCount = result.StarterAccountCodes.Count,
                    provisionedAtUtc = result.ProvisionedAtUtc
                });
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await store.ListAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
                return Results.Ok(rows);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArCustomerView);

        accounting.MapGet(
            "/customers/{customerId:guid}",
            async (
                Guid customerId,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var customer = await store.GetByIdAsync(session.ActiveCompanyId, customerId, cancellationToken);
                return customer is null ? Results.NotFound() : Results.Ok(customer);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArCustomerView);

        accounting.MapPost(
            "/customers",
            async (
                CustomerUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerStore store,
                ICompanyCurrencyGovernanceWorkflow currencyWorkflow,
                Citus.Modules.UnitySearch.Application.Contracts.IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                    // Picking a foreign currency for a counterparty is the
                    // operator's commitment to do business in that currency, so
                    // make sure the per-currency AR/AP control accounts are
                    // provisioned before the row goes in. EnableCurrencyAsync is
                    // idempotent: same-as-base or already-enabled currencies
                    // return the existing profile without inserting anything,
                    // so it's safe to call unconditionally.
                    await currencyWorkflow.EnableCurrencyAsync(
                        session.ActiveCompanyId,
                        request.DefaultCurrencyCode,
                        session.UserId,
                        cancellationToken);

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
                    // Invalidate the unity-search projection so the new customer
                    // shows up in the topbar / customer pickers immediately
                    // (otherwise operators wait out the 5-min refresh window).
                    await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArCustomerCreate);

        accounting.MapPut(
            "/customers/{customerId:guid}",
            async (
                Guid customerId,
                CustomerUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerStore store,
                Citus.Modules.UnitySearch.Application.Contracts.IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                    if (saved is not null)
                    {
                        // Display-name / status edits flow into the search index so
                        // the projection cache needs to drop too.
                        await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
                    }
                    return saved is null ? Results.NotFound() : Results.Ok(saved);
                }
                catch (PostgresException ex) when (ex.SqlState == "23503")
                {
                    return Results.BadRequest(new { message = $"Currency '{request.DefaultCurrencyCode}' is not available in this company. Enable it in Settings → Multi-currency." });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArCustomerEdit);

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await store.ListAsync(session.ActiveCompanyId, includeInactive ?? false, cancellationToken);
                return Results.Ok(rows);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorView);

        accounting.MapGet(
            "/vendors/{vendorId:guid}",
            async (
                Guid vendorId,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var vendor = await store.GetByIdAsync(session.ActiveCompanyId, vendorId, cancellationToken);
                return vendor is null ? Results.NotFound() : Results.Ok(vendor);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorView);

        accounting.MapPost(
            "/vendors",
            async (
                VendorUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorStore store,
                ICompanyCurrencyGovernanceWorkflow currencyWorkflow,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                    // Mirror of the customer create route: provision per-currency
                    // AP control accounts the moment a vendor is recorded in a
                    // foreign currency. Idempotent — same-as-base or already-
                    // enabled currencies just return the existing profile.
                    await currencyWorkflow.EnableCurrencyAsync(
                        session.ActiveCompanyId,
                        request.DefaultCurrencyCode,
                        session.UserId,
                        cancellationToken);

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
                    // H15: drop the projection's "last refreshed" timestamp so the
                    // new vendor surfaces in the topbar / vendor pickers on the
                    // next search instead of waiting out the 5-minute refresh
                    // window.
                    await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorCreate);

        accounting.MapPut(
            "/vendors/{vendorId:guid}",
            async (
                Guid vendorId,
                VendorUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorStore store,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                    if (saved is not null)
                    {
                        // H15: keep topbar / vendor picker in sync with the rename
                        // / address update without waiting for the projection's
                        // 5-minute refresh window.
                        await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
                    }
                    return saved is null ? Results.NotFound() : Results.Ok(saved);
                }
                catch (PostgresException ex) when (ex.SqlState == "23503")
                {
                    return Results.BadRequest(new { message = $"Currency '{request.DefaultCurrencyCode}' is not available in this company. Enable it in Settings → Multi-currency." });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorEdit);
    }
}
