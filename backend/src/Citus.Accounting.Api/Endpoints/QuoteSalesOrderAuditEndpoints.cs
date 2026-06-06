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
/// QuoteSalesOrderAudit endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class QuoteSalesOrderAuditEndpoints
{
    public static void MapQuoteSalesOrderAuditEndpoints(this RouteGroupBuilder accounting)
    {

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                catch (ConcurrencyConflictException ex)
                {
                    return Results.Conflict(new
                    {
                        code = "concurrency_conflict",
                        message = ex.Message,
                    });
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                    CustomerPoNumber: quote.CustomerPoNumber,
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                catch (ConcurrencyConflictException ex)
                {
                    return Results.Conflict(new
                    {
                        code = "concurrency_conflict",
                        message = ex.Message,
                    });
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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

        // M5 iter 1: confirm an Open SO. Splits each line's qty into reserved /
        // backorder based on current item_warehouse_balances.available, bumps
        // reserved_qty on the balance, and flips status to 'confirmed'. Service
        // or non-stock items skip reservation. Items with backorder_mode='disallow'
        // fail the confirm with an InvalidOperationException so the operator sees
        // a precise shortage message.
        accounting.MapPost(
            "/sales-orders/{salesOrderId:guid}/confirm",
            async (
                Guid salesOrderId,
                BusinessSessionContextAccessor sessionAccessor,
                ISalesOrderStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                try
                {
                    var saved = await store.ConfirmAsync(session.ActiveCompanyId, salesOrderId, cancellationToken);
                    return saved is null ? Results.NotFound() : Results.Ok(saved);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        // M5 wrap-up: read-side projection of customer deposits scoped to an SO.
        // Drives the deposit balance panel on SO detail (collected / applied /
        // remaining + per-deposit table). Returns an empty summary (zero totals,
        // empty list) when the SO has no deposits, so the UI can render "—" cleanly.
        accounting.MapGet(
            "/sales-orders/{salesOrderId:guid}/deposits",
            async (
                Guid salesOrderId,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerDepositReader reader,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var summary = await reader.GetForSalesOrderAsync(session.ActiveCompanyId, salesOrderId, cancellationToken);
                return Results.Ok(summary);
            });

        // M5 iter 5: SO cancellation orchestrator. Releases reservations on
        // confirmed SOs, zeroes per-line counters, flips status to 'cancelled',
        // and surfaces a deposit warning if any open customer_deposits still
        // point at this SO (V1 doesn't auto-refund — operator handles via
        // Refund Receipt).
        accounting.MapPost(
            "/sales-orders/{salesOrderId:guid}/cancel",
            async (
                Guid salesOrderId,
                BusinessSessionContextAccessor sessionAccessor,
                ISalesOrderStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                try
                {
                    var result = await store.CancelAsync(session.ActiveCompanyId, salesOrderId, cancellationToken);
                    if (result is null) return Results.NotFound();
                    return Results.Ok(new
                    {
                        salesOrder = result.SalesOrder,
                        openDepositCount = result.OpenDepositSummary.OpenDepositCount,
                        openDepositTotalBase = result.OpenDepositSummary.TotalOpenAmountBase,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        // M5 iter 3: standalone Customer Deposit on an SO. Operator collects a
        // prepayment against an open / confirmed SO before any invoice exists.
        // Persists customer_deposits + ar_open_items credit row + posts JE
        // (Dr Bank / Cr Customer Deposit) in a single unit of work.
        accounting.MapPost(
            "/sales-orders/{salesOrderId:guid}/deposit",
            async (
                Guid salesOrderId,
                SalesOrderDepositHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ISalesOrderStore salesOrderStore,
                PostCustomerDepositCommandHandler handler,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                if (request.AmountTx <= 0m) return Results.BadRequest(new { message = "Deposit amount must be positive." });
                if (request.DepositToAccountId == Guid.Empty) return Results.BadRequest(new { message = "Deposit-to (bank) account is required." });

                // Resolve customer from the SO so the client doesn't have to ferry
                // it (and so we never create a deposit pointed at the wrong customer).
                var so = await salesOrderStore.GetByIdAsync(session.ActiveCompanyId, salesOrderId, cancellationToken);
                if (so is null) return Results.NotFound(new { message = "Sales order not found in the active company." });

                try
                {
                    var result = await handler.HandleAsync(
                        new PostCustomerDepositCommand(
                            session.ActiveCompanyId,
                            session.UserId,
                            SalesOrderId: salesOrderId,
                            CustomerId: so.CustomerId,
                            DepositToAccountId: request.DepositToAccountId,
                            AmountTx: request.AmountTx,
                            DocumentDate: request.DocumentDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                            Memo: request.Memo,
                            IdempotencyKey: ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        // Audit log reader. Read-side only — every audit row is written by the
        // path that emitted the action (membership change, period transition,
        // adjustment approval, etc.). Filters scope the result to a sane
        // window so a busy company doesn't dump months of activity by
        // accident; the page surfaces sensible defaults (last 7 days,
        // limit 200).
        accounting.MapGet(
            "/audit-logs",
            async (
                DateTimeOffset? since,
                string? action,
                string? entityType,
                int? limit,
                BusinessSessionContextAccessor sessionAccessor,
                IAuditLogReader reader,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var query = new AuditLogQuery(
                    Since: since ?? DateTimeOffset.UtcNow.AddDays(-7),
                    Action: action,
                    EntityType: entityType,
                    Limit: limit ?? 200);
                var rows = await reader.ListAsync(session.ActiveCompanyId, query, cancellationToken);
                return Results.Ok(rows);
            });

        // M7 iter 4: year-end pre-close checks. Returns three soft-block
        // counters the dashboard surfaces before the operator transitions
        // the most recent period through closing -> closed. Read-only;
        // each non-zero count is informational and operators decide how to
        // resolve.
        accounting.MapGet(
            "/year-end/pre-close-checks",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IYearEndPreCloseChecksReader reader,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var checks = await reader.ReadAsync(session.ActiveCompanyId, cancellationToken);
                return Results.Ok(checks);
            });

        // M7 iter 1: accounting period state machine endpoints. List returns
        // every period for the active company (lazy-seeds the current fiscal
        // year of monthly periods on first call). Transition flips status
        // forward through the open → closing → closed → locked path with an
        // audit-log entry; gated to owner / book-governance roles.
        accounting.MapGet(
            "/periods",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IAccountingPeriodRepository periods,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var rows = await periods.ListAsync(session.ActiveCompanyId, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapPost(
            "/periods/{periodId:guid}/transition",
            async (
                Guid periodId,
                AccountingPeriodTransitionHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountingPeriodRepository periods,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (request is null || string.IsNullOrWhiteSpace(request.TargetStatus))
                {
                    return Results.BadRequest(new { message = "TargetStatus is required." });
                }
                if (!BusinessApprovalAuthority.CanTransitionAccountingPeriod(session))
                {
                    return Results.BadRequest(new { message = "Only a company owner or book-governance user can transition accounting periods." });
                }

                try
                {
                    var updated = await periods.TransitionAsync(
                        session.ActiveCompanyId,
                        session.UserId,
                        periodId,
                        request.TargetStatus.Trim().ToLowerInvariant(),
                        cancellationToken);
                    return Results.Ok(updated);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        // M6 iter 4: drop-ship clearing aging workbench. Per-item rollup of
        // posted bill clearing-debits vs posted invoice COGS clearing-credits.
        // Read-only — write-off action is a sister POST endpoint below.
        accounting.MapGet(
            "/inventory/drop-ship-clearing/aging",
            async (
                bool? hideBalanced,
                BusinessSessionContextAccessor sessionAccessor,
                IDropShipClearingAgingReader reader,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await reader.ListAsync(
                    session.ActiveCompanyId,
                    hideBalanced ?? false,
                    cancellationToken);
                return Results.Ok(rows);
            });

        // M6 iter 4: write off the residual on Drop-ship Clearing for one item.
        // Body carries the operator's expected residual; the server re-reads
        // the live amount and rejects the write-off if they disagree (concurrent
        // activity protection). The expected sign decides which side hits the
        // clearing — both lead to a zero balance on the clearing for that item.
        accounting.MapPost(
            "/inventory/drop-ship-clearing/{itemId:guid}/write-off",
            async (
                Guid itemId,
                DropShipClearingWriteOffHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                WriteOffDropShipClearingCommandHandler handler,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (request is null) return Results.BadRequest(new { message = "Request body is required." });

                try
                {
                    var result = await handler.HandleAsync(
                        new WriteOffDropShipClearingCommand(
                            session.ActiveCompanyId,
                            session.UserId,
                            ItemId: itemId,
                            ExpectedNetClearingBase: request.ExpectedNetClearingBase,
                            Memo: request.Memo,
                            IdempotencyKey: ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);
    }
}
