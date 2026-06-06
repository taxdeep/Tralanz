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
/// ApBillsPoExpenses endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class ApBillsPoExpensesEndpoints
{
    public static void MapApBillsPoExpensesEndpoints(this RouteGroupBuilder accounting)
    {

        // ---------------------------------------------------------------------------
        // Maps an InvoiceTemplate domain record into the wire shape that the
        // Settings UI consumes. Flat structure (no nested config object) so a
        // JSON typed-deserialize on the client stays trivial.
        // ---------------------------------------------------------------------------
        // ---------------------------------------------------------------------------
        // Validates and maps the wire-format upsert request into the Application-
        // layer InvoiceTemplateConfig. Returns the parsed config plus a non-null
        // error string when validation fails.
        // ---------------------------------------------------------------------------
        // ---------------------------------------------------------------------------
        // Synthesizes a stand-in invoice projection for the template preview
        // endpoint so the editor can render a real PDF before any actual invoice
        // exists. Numbers / dates / line text are deliberately recognizable as
        // sample data ("INV-PREVIEW", "Acme Co.") so an operator who downloads
        // it doesn't mistake the preview for a real document.
        // ---------------------------------------------------------------------------
        // ===========================================================================
        // Bills (vendor invoices) — AP-side document lifecycle.
        //
        // Legacy surface: list / get / create-as-draft / edit-draft / draft void.
        // Posting is intentionally blocked here. Bills must not become "posted"
        // unless the canonical posting engine writes the journal and AP open item.
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var bill = await store.GetByIdAsync(session.ActiveCompanyId, billId, cancellationToken);
                return bill is null ? Results.NotFound() : Results.Ok(bill);
            });

        accounting.MapPost(
            "/ap/bills",
            async (
                BillUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IBillStore store,
                ITaskLineLinkValidator taskLinkValidator,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                var validation = ValidateBillInput(request);
                if (validation is not null) return Results.BadRequest(new { message = validation });

                try
                {
                    // Validate task links before insert. Rejects billed /
                    // canceled / cross-company tasks so the link can never
                    // settle on a non-attributable target.
                    await ValidateBillExpenseTaskLinksAsync(
                        taskLinkValidator,
                        session.ActiveCompanyId,
                        (request.Lines ?? Array.Empty<BillLineHttpRequest>()).Select(l => l.TaskId),
                        cancellationToken);

                    var saved = await store.CreateAsync(
                        session.ActiveCompanyId,
                        session.UserId,
                        MapBillInput(request),
                        cancellationToken);
                    return Results.Ok(saved);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
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
                ITaskLineLinkValidator taskLinkValidator,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var validation = ValidateBillInput(request);
                if (validation is not null) return Results.BadRequest(new { message = validation });

                try
                {
                    await ValidateBillExpenseTaskLinksAsync(
                        taskLinkValidator,
                        session.ActiveCompanyId,
                        (request.Lines ?? Array.Empty<BillLineHttpRequest>()).Select(l => l.TaskId),
                        cancellationToken);

                    var saved = await store.UpdateAsync(
                        session.ActiveCompanyId,
                        billId,
                        MapBillInput(request),
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
                catch (PostgresException ex) when (ex.SqlState == "23505")
                {
                    return Results.BadRequest(new { message = $"Bill number '{request.BillNumber}' already exists for this vendor / company." });
                }
            });

        accounting.MapPost(
            "/ap/bills/{billId:guid}/post",
            async (
                Guid billId,
                CancellationToken cancellationToken) =>
            {
                await Task.CompletedTask;
                return Results.Conflict(new
                {
                    code = "legacy_bill_posting_disabled",
                    message = "Bill posting is disabled on this legacy Bills page because it would not create the required journal entry and AP open item. Use the canonical bill posting workflow."
                });
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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

        /// <summary>
        /// Runs <see cref="ITaskLineLinkValidator"/> against the distinct
        /// non-null Task ids on a bill / expense upsert. No-ops when no lines
        /// carry a task. Throws <see cref="InvalidOperationException"/> on
        /// the first invalid link — the route catches it and returns 400 with
        /// the validator's user-facing message.
        /// </summary>
        // ===========================================================================
        // Purchase Orders (AP-side, /ap/purchase-orders) — pre-bill commitments.
        //
        // Uses the brand-neutral global::Modules.AP.PurchaseOrders module backed by the
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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
            "/ap/purchase-orders/{purchaseOrderId:guid}/status",
            async (
                Guid purchaseOrderId,
                PurchaseOrderStatusHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IPurchaseOrderStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var expense = await store.GetByIdAsync(session.ActiveCompanyId, expenseId, cancellationToken);
                return expense is null ? Results.NotFound() : Results.Ok(expense);
            });

        accounting.MapPost(
            "/ap/expenses",
            async (
                ExpenseUpsertHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IExpenseStore store,
                ITaskLineLinkValidator taskLinkValidator,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                var validation = ValidateExpenseInput(request);
                if (validation is not null) return Results.BadRequest(new { message = validation });

                try
                {
                    await ValidateBillExpenseTaskLinksAsync(
                        taskLinkValidator,
                        session.ActiveCompanyId,
                        (request.Lines ?? Array.Empty<ExpenseLineHttpRequest>()).Select(l => l.TaskId),
                        cancellationToken);

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
                PostExpenseVoidCommandHandler voidHandler,
                IExpenseStore store,
                CancellationToken cancellationToken) =>
            {
                // H1: route the compensating Dr Payment / Cr Expense JE through the
                // Posting Engine first (idempotent via source_type='expense_void' +
                // source_id probe). Only after the JE is committed do we flip the
                // expense row to 'voided'. Two transactions, but the handler is safe
                // to retry: a repeat returns AlreadyVoided=true without re-posting.
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    await voidHandler.HandleAsync(
                        new PostExpenseVoidCommand(
                            session.ActiveCompanyId,
                            session.UserId,
                            expenseId),
                        cancellationToken);

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
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

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
    }
}
