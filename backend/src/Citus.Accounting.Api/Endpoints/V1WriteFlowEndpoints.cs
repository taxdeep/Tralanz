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
/// V1WriteFlow endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class V1WriteFlowEndpoints
{
    public static void MapV1WriteFlowEndpoints(this RouteGroupBuilder accounting)
    {

        // ============================================================================
        // V1-pending document write flows.
        //
        // Frontend pages for Sales Receipt, Refund Receipt, Credit Memo,
        // Vendor Credit, Bank Transfer, Bank Deposit, and Tax Return are
        // fully wired and validated; the matching repositories + posting
        // engine fragments are the next backend round. Each endpoint here:
        //   1. Validates the request body shape.
        //   2. Logs the attempted post for observability.
        //   3. Returns 501 Not Implemented with a structured body so the
        //      frontend can render its "simulation only" banner with the
        //      same wire contract the real round-trip will eventually use.
        //
        // When the repositories ship, the body of each endpoint changes to
        // a real save-and-post call; the URL and request shape stay the same,
        // so the frontend doesn't move.
        // ============================================================================

        accounting.MapPost(
            "/sales-receipts/save-and-post",
            async (
                SalesReceiptSaveAndPostHttpRequest request,
                ISalesReceiptDocumentRepository repository,
                PostSalesReceiptCommandHandler postHandler,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                // 1. Validate the wire shape. Frontend already does its own
                //    pass; this is the authoritative server-side gate.
                if (request.CompanyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                if (request.UserId.Value is null) return Results.BadRequest(new { error = "userId required" });
                if (request.CustomerId == Guid.Empty) return Results.BadRequest(new { error = "customerId required" });
                if (request.DepositToAccountId == Guid.Empty) return Results.BadRequest(new { error = "depositToAccountId required" });
                if (request.Lines.Count == 0) return Results.BadRequest(new { error = "at least one line required" });
                foreach (var line in request.Lines)
                {
                    if (line.RevenueAccountId == Guid.Empty)
                    {
                        return Results.BadRequest(new { error = $"line {line.LineNumber}: revenueAccountId required" });
                    }
                    if (line.Quantity <= 0m)
                    {
                        return Results.BadRequest(new { error = $"line {line.LineNumber}: quantity must be positive" });
                    }
                }

                try
                {
                    // 2. Save the draft (status='draft', lines persisted).
                    var saveResult = await repository.SaveDraftAsync(
                        new SalesReceiptDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.CustomerId,
                            request.DepositToAccountId,
                            request.PaymentMethod,
                            request.ReferenceNo,
                            request.DocumentDate,
                            string.IsNullOrWhiteSpace(request.TransactionCurrencyCode) ? "USD" : request.TransactionCurrencyCode,
                            string.IsNullOrWhiteSpace(request.BaseCurrencyCode) ? request.TransactionCurrencyCode : request.BaseCurrencyCode,
                            null,
                            request.FxRate,
                            null,
                            null,
                            request.Memo,
                            request.Lines.Select(line => new SalesReceiptDraftLineSaveModel(
                                line.LineNumber,
                                line.RevenueAccountId,
                                line.Description,
                                line.Quantity,
                                line.UnitPrice,
                                line.TaxCodeId,
                                line.TaxAmount,
                                ItemId: null,
                                TaskId: line.TaskId,
                                TaskLineId: line.TaskLineId)).ToArray(),
                            string.IsNullOrWhiteSpace(request.CustomerPoNumber) ? null : request.CustomerPoNumber.Trim()),
                        cancellationToken);

                    // 3. Post the draft → PostingEngine writes the journal,
                    //    JournalEntryWriter flips status to 'posted'.
                    var postResult = await postHandler.HandleAsync(
                        new PostSalesReceiptCommand(
                            request.CompanyId,
                            saveResult.DocumentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-c: sales receipt status flipped draft → posted; refresh the
                    // projection so the receipt surfaces in topbar search immediately.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);

                    return Results.Ok(new
                    {
                        documentId = saveResult.DocumentId,
                        receiptNumber = saveResult.DisplayNumber,
                        entityNumber = saveResult.EntityNumber,
                        status = postResult.Status,
                        journalEntryId = postResult.JournalEntryId,
                        journalEntryDisplayNumber = postResult.JournalEntryDisplayNumber,
                        postedAt = postResult.PostedAt,
                        warnings = postResult.Warnings,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArInvoicePost);

        accounting.MapPost(
            "/refund-receipts/save-and-post",
            async (
                RefundReceiptSaveAndPostHttpRequest request,
                IRefundReceiptDocumentRepository repository,
                PostRefundReceiptCommandHandler postHandler,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                if (request.CompanyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                if (request.UserId.Value is null) return Results.BadRequest(new { error = "userId required" });
                if (request.CustomerId == Guid.Empty) return Results.BadRequest(new { error = "customerId required" });
                if (request.RefundFromAccountId == Guid.Empty) return Results.BadRequest(new { error = "refundFromAccountId required" });
                if (request.Lines.Count == 0) return Results.BadRequest(new { error = "at least one line required" });
                foreach (var line in request.Lines)
                {
                    if (line.RevenueAccountId == Guid.Empty)
                    {
                        return Results.BadRequest(new { error = $"line {line.LineNumber}: revenueAccountId required" });
                    }
                    if (line.Quantity <= 0m)
                    {
                        return Results.BadRequest(new { error = $"line {line.LineNumber}: quantity must be positive" });
                    }
                }

                try
                {
                    var saveResult = await repository.SaveDraftAsync(
                        new RefundReceiptDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.CustomerId,
                            request.RefundFromAccountId,
                            request.PaymentMethod,
                            request.ReferenceNo,
                            request.Reason,
                            request.DocumentDate,
                            string.IsNullOrWhiteSpace(request.TransactionCurrencyCode) ? "USD" : request.TransactionCurrencyCode,
                            string.IsNullOrWhiteSpace(request.BaseCurrencyCode) ? request.TransactionCurrencyCode : request.BaseCurrencyCode,
                            null,
                            request.FxRate,
                            null,
                            null,
                            request.Memo,
                            request.Lines.Select(line => new RefundReceiptDraftLineSaveModel(
                                line.LineNumber,
                                line.RevenueAccountId,
                                line.Description,
                                line.Quantity,
                                line.UnitPrice,
                                line.TaxCodeId,
                                line.TaxAmount,
                                ItemId: null,
                                TaskId: line.TaskId,
                                TaskLineId: line.TaskLineId)).ToArray(),
                            string.IsNullOrWhiteSpace(request.CustomerPoNumber) ? null : request.CustomerPoNumber.Trim()),
                        cancellationToken);

                    var postResult = await postHandler.HandleAsync(
                        new PostRefundReceiptCommand(
                            request.CompanyId,
                            saveResult.DocumentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-c: refund receipt status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);

                    return Results.Ok(new
                    {
                        documentId = saveResult.DocumentId,
                        refundNumber = saveResult.DisplayNumber,
                        entityNumber = saveResult.EntityNumber,
                        status = postResult.Status,
                        journalEntryId = postResult.JournalEntryId,
                        journalEntryDisplayNumber = postResult.JournalEntryDisplayNumber,
                        postedAt = postResult.PostedAt,
                        warnings = postResult.Warnings,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArInvoicePost);

        accounting.MapPost(
            "/credit-memos/save-and-post",
            async (
                CreditMemoSaveAndPostHttpRequest request,
                ICreditNoteDocumentRepository repository,
                PostCreditNoteCommandHandler postHandler,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                // CreditMemo reuses the existing credit_notes infrastructure
                // (table + repository + posting fragment). The frontend uses
                // the term "credit memo" because that's the QBO-flavoured
                // operator label; the GL artifact stays a credit_note.
                //
                // V1 always issues a standalone credit (no apply-to-invoice
                // wiring at this endpoint). When an apply-against-an-invoice
                // is needed, the operator runs Receive Payment with the
                // credit listed alongside the invoice in the open-item
                // picker — that path already exists.
                if (request.CompanyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                if (request.UserId.Value is null) return Results.BadRequest(new { error = "userId required" });
                if (request.CustomerId == Guid.Empty) return Results.BadRequest(new { error = "customerId required" });
                if (request.Lines.Count == 0) return Results.BadRequest(new { error = "at least one line required" });
                foreach (var line in request.Lines)
                {
                    if (line.RevenueAccountId == Guid.Empty)
                    {
                        return Results.BadRequest(new { error = $"line {line.LineNumber}: revenueAccountId required" });
                    }
                    if (line.Quantity <= 0m)
                    {
                        return Results.BadRequest(new { error = $"line {line.LineNumber}: quantity must be positive" });
                    }
                }

                try
                {
                    var saveResult = await repository.SaveDraftAsync(
                        new CreditNoteDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.CustomerId,
                            request.DocumentDate,
                            // Credit memos don't carry a "due date" the way
                            // invoices do — the customer doesn't owe anything
                            // (they're owed). Default it to the credit-note
                            // date so the underlying credit_notes row stays
                            // schema-valid.
                            request.DocumentDate,
                            string.IsNullOrWhiteSpace(request.TransactionCurrencyCode) ? "USD" : request.TransactionCurrencyCode,
                            string.IsNullOrWhiteSpace(request.BaseCurrencyCode) ? request.TransactionCurrencyCode : request.BaseCurrencyCode,
                            null,
                            request.FxRate,
                            null,
                            null,
                            BuildCreditMemoMemo(request),
                            request.Lines.Select(line => new CreditNoteDraftLineSaveModel(
                                line.LineNumber,
                                line.RevenueAccountId,
                                line.Description,
                                line.Quantity,
                                line.UnitPrice,
                                line.TaxCodeId,
                                line.TaxAmount,
                                line.TaskId,
                                line.TaskLineId)).ToArray(),
                            string.IsNullOrWhiteSpace(request.CustomerPoNumber) ? null : request.CustomerPoNumber.Trim()),
                        cancellationToken);

                    var postResult = await postHandler.HandleAsync(
                        new PostCreditNoteCommand(
                            request.CompanyId,
                            saveResult.DocumentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-c: credit memo status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);

                    return Results.Ok(new
                    {
                        documentId = saveResult.DocumentId,
                        creditMemoNumber = saveResult.DisplayNumber,
                        entityNumber = saveResult.EntityNumber,
                        status = postResult.Status,
                        journalEntryId = postResult.JournalEntryId,
                        journalEntryDisplayNumber = postResult.JournalEntryDisplayNumber,
                        postedAt = postResult.PostedAt,
                        warnings = postResult.Warnings,
                        appliedToInvoiceNumber = string.IsNullOrWhiteSpace(request.AppliedToInvoiceNumber) ? null : request.AppliedToInvoiceNumber,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArCreditNotePost);

        // Combine the operator's free-text Reason + Memo + AppliedTo hint into
        // one persistent memo line on the credit_notes row. Future iteration
        // adds explicit columns for these once the credit_notes schema is
        // extended; for V1 this is a lossless round-trip via memo.
        accounting.MapPost(
            "/vendor-credits/save-and-post",
            async (
                VendorCreditSaveAndPostHttpRequest request,
                IVendorCreditDocumentRepository repository,
                PostVendorCreditCommandHandler postHandler,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                // Sister to /credit-memos/save-and-post on the AP side. The
                // vendor_credits table + repository + posting fragment +
                // AP open-item handler all already exist; this endpoint is
                // pure adapter work mapping the V1-pending wire shape to the
                // existing draft-save-model.
                if (request.CompanyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                if (request.UserId.Value is null) return Results.BadRequest(new { error = "userId required" });
                if (request.VendorId == Guid.Empty) return Results.BadRequest(new { error = "vendorId required" });
                if (request.Lines.Count == 0) return Results.BadRequest(new { error = "at least one line required" });
                foreach (var line in request.Lines)
                {
                    if (line.ExpenseAccountId == Guid.Empty)
                    {
                        return Results.BadRequest(new { error = $"line {line.LineNumber}: expenseAccountId required" });
                    }
                    if (line.LineAmount <= 0m)
                    {
                        return Results.BadRequest(new { error = $"line {line.LineNumber}: lineAmount must be positive" });
                    }
                }

                try
                {
                    var saveResult = await repository.SaveDraftAsync(
                        new VendorCreditDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.DocumentDate,
                            // VendorCredit row schema requires due_date; for a
                            // standalone vendor credit there's nothing to "be
                            // due", so we mirror the credit-note convention
                            // and set it to the document date.
                            request.DocumentDate,
                            string.IsNullOrWhiteSpace(request.TransactionCurrencyCode) ? "USD" : request.TransactionCurrencyCode,
                            string.IsNullOrWhiteSpace(request.BaseCurrencyCode) ? request.TransactionCurrencyCode : request.BaseCurrencyCode,
                            null,
                            request.FxRate,
                            null,
                            null,
                            BuildVendorCreditMemo(request),
                            request.Lines.Select(line => new VendorCreditDraftLineSaveModel(
                                line.LineNumber,
                                line.ExpenseAccountId,
                                line.Description,
                                line.LineAmount,
                                line.TaxCodeId,
                                line.TaxAmount,
                                // V1 default: every vendor credit's input-tax
                                // is recoverable (treated as ITC reversal).
                                // Future iteration exposes a checkbox on the
                                // line for non-recoverable tax (capital goods
                                // edge cases, etc.).
                                IsTaxRecoverable: true)).ToArray()),
                        cancellationToken);

                    var postResult = await postHandler.HandleAsync(
                        new PostVendorCreditCommand(
                            request.CompanyId,
                            saveResult.DocumentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-c: vendor credit status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);

                    return Results.Ok(new
                    {
                        documentId = saveResult.DocumentId,
                        vendorCreditNumber = saveResult.DisplayNumber,
                        entityNumber = saveResult.EntityNumber,
                        status = postResult.Status,
                        journalEntryId = postResult.JournalEntryId,
                        journalEntryDisplayNumber = postResult.JournalEntryDisplayNumber,
                        postedAt = postResult.PostedAt,
                        warnings = postResult.Warnings,
                        appliedToBillNumber = string.IsNullOrWhiteSpace(request.AppliedToBillNumber) ? null : request.AppliedToBillNumber,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorCreditPost);

        accounting.MapPost(
            "/bank-transfers/save-and-post",
            async (
                BankTransferSaveAndPostHttpRequest request,
                IBankTransferDocumentRepository repository,
                PostBankTransferCommandHandler postHandler,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                if (request.CompanyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                if (request.UserId.Value is null) return Results.BadRequest(new { error = "userId required" });
                if (request.FromAccountId == Guid.Empty) return Results.BadRequest(new { error = "fromAccountId required" });
                if (request.ToAccountId == Guid.Empty) return Results.BadRequest(new { error = "toAccountId required" });
                if (request.FromAccountId == request.ToAccountId) return Results.BadRequest(new { error = "from and to must be different accounts" });
                if (request.Amount <= 0) return Results.BadRequest(new { error = "amount must be positive" });
                var sameCurrency = string.Equals(request.FromCurrencyCode, request.ToCurrencyCode, StringComparison.OrdinalIgnoreCase);
                if (sameCurrency && request.FxRate.HasValue) return Results.BadRequest(new { error = "fxRate must be null on same-currency transfers" });
                if (!sameCurrency && (!request.FxRate.HasValue || request.FxRate.Value <= 0)) return Results.BadRequest(new { error = "fxRate required and positive on cross-currency transfers" });

                try
                {
                    var saveResult = await repository.SaveDraftAsync(
                        new BankTransferDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.DocumentDate,
                            request.FromAccountId,
                            request.FromCurrencyCode,
                            request.ToAccountId,
                            request.ToCurrencyCode,
                            request.Amount,
                            request.FxRate,
                            null,
                            null,
                            null,
                            request.ReferenceNo,
                            request.Memo),
                        cancellationToken);

                    var postResult = await postHandler.HandleAsync(
                        new PostBankTransferCommand(
                            request.CompanyId,
                            saveResult.DocumentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-c: bank transfer status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);

                    return Results.Ok(new
                    {
                        documentId = saveResult.DocumentId,
                        transferNumber = saveResult.DisplayNumber,
                        entityNumber = saveResult.EntityNumber,
                        status = postResult.Status,
                        journalEntryId = postResult.JournalEntryId,
                        journalEntryDisplayNumber = postResult.JournalEntryDisplayNumber,
                        postedAt = postResult.PostedAt,
                        warnings = postResult.Warnings,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlJournalPost);

        accounting.MapPost(
            "/bank-deposits/save-and-post",
            async (
                BankDepositSaveAndPostHttpRequest request,
                IBankDepositDocumentRepository repository,
                PostBankDepositCommandHandler postHandler,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                if (request.CompanyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                if (request.UserId.Value is null) return Results.BadRequest(new { error = "userId required" });
                if (request.DepositToAccountId == Guid.Empty) return Results.BadRequest(new { error = "depositToAccountId required" });
                if (request.Items.Count == 0) return Results.BadRequest(new { error = "at least one item required" });
                if (request.Items.Any(i => string.IsNullOrWhiteSpace(i.SourceItemDisplayNumber))) return Results.BadRequest(new { error = "every item needs a sourceItemDisplayNumber" });
                if (request.Items.Any(i => i.Amount <= 0)) return Results.BadRequest(new { error = "every item amount must be positive" });

                try
                {
                    var saveResult = await repository.SaveDraftAsync(
                        new BankDepositDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.DocumentDate,
                            request.DepositToAccountId,
                            string.IsNullOrWhiteSpace(request.TransactionCurrencyCode) ? "USD" : request.TransactionCurrencyCode,
                            request.ReferenceNo,
                            request.Memo,
                            request.Items.Select((item, idx) => new BankDepositItemDraftSaveModel(
                                idx + 1,
                                // V1 default kind: "manual" (operator typed
                                // a free-form display number). When the
                                // Undeposited-Funds picker ships, the kind
                                // ('sales_receipt' / 'receive_payment') will
                                // come from the picker option itself.
                                "manual",
                                item.SourceItemId == Guid.Empty ? null : item.SourceItemId,
                                item.SourceItemDisplayNumber,
                                item.PayerName,
                                item.PaymentMethod,
                                item.ReferenceNo,
                                item.Amount)).ToArray()),
                        cancellationToken);

                    var postResult = await postHandler.HandleAsync(
                        new PostBankDepositCommand(
                            request.CompanyId,
                            saveResult.DocumentId,
                            request.UserId,
                            null,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-c: bank deposit status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);

                    return Results.Ok(new
                    {
                        documentId = saveResult.DocumentId,
                        depositNumber = saveResult.DisplayNumber,
                        entityNumber = saveResult.EntityNumber,
                        status = postResult.Status,
                        journalEntryId = postResult.JournalEntryId,
                        journalEntryDisplayNumber = postResult.JournalEntryDisplayNumber,
                        postedAt = postResult.PostedAt,
                        warnings = postResult.Warnings,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlJournalPost);

        accounting.MapPost(
            "/tax-returns/save-and-post",
            async (
                TaxReturnSaveAndPostHttpRequest request,
                ITaxReturnDocumentRepository repository,
                PostTaxReturnCommandHandler postHandler,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                if (request.CompanyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                if (request.UserId.Value is null) return Results.BadRequest(new { error = "userId required" });
                if (request.PeriodEnd < request.PeriodStart) return Results.BadRequest(new { error = "periodEnd must be on or after periodStart" });
                if (string.IsNullOrWhiteSpace(request.TaxRegime)) return Results.BadRequest(new { error = "taxRegime required" });
                if (string.IsNullOrWhiteSpace(request.FilingFrequency)) return Results.BadRequest(new { error = "filingFrequency required" });
                if (string.IsNullOrWhiteSpace(request.BaseCurrencyCode)) return Results.BadRequest(new { error = "baseCurrencyCode required" });
                if (request.CollectedAmount < 0m || request.InputCreditsAmount < 0m)
                {
                    return Results.BadRequest(new { error = "collected and ITC amounts must be non-negative" });
                }

                try
                {
                    var baseCurrency = request.BaseCurrencyCode;

                    var saveResult = await repository.SaveDraftAsync(
                        new TaxReturnDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.TaxRegime,
                            request.FilingFrequency,
                            request.PeriodStart,
                            request.PeriodEnd,
                            baseCurrency,
                            request.CollectedAmount,
                            request.InputCreditsAmount,
                            request.AdjustmentsAmount,
                            request.AdjustmentsNote,
                            request.RegulatorReferenceNo,
                            request.Memo),
                        cancellationToken);

                    var postResult = await postHandler.HandleAsync(
                        new PostTaxReturnCommand(
                            request.CompanyId,
                            saveResult.DocumentId,
                            request.UserId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-c: tax return status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);

                    return Results.Ok(new
                    {
                        documentId = saveResult.DocumentId,
                        returnNumber = saveResult.DisplayNumber,
                        entityNumber = saveResult.EntityNumber,
                        status = postResult.Status,
                        journalEntryId = postResult.JournalEntryId,
                        journalEntryDisplayNumber = postResult.JournalEntryDisplayNumber,
                        postedAt = postResult.PostedAt,
                        warnings = postResult.Warnings,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlJournalPost);

        // ============================================================================
        // V1 list endpoints for the 7 doc types that now post end-to-end.
        // Each calls the repository's ListAsync to surface a summary feed
        // (most-recent first, capped at 200 rows). The companyId comes off
        // the query string the same way the detail endpoints get it.
        // ============================================================================

        accounting.MapGet(
            "/sales-receipts",
            async (CompanyId companyId, bool? includeDrafts, ISalesReceiptDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                if (companyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                var rows = await repository.ListAsync(companyId, includeDrafts ?? true, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapGet(
            "/refund-receipts",
            async (CompanyId companyId, bool? includeDrafts, IRefundReceiptDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                if (companyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                var rows = await repository.ListAsync(companyId, includeDrafts ?? true, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapGet(
            "/credit-memos",
            async (CompanyId companyId, bool? includeDrafts, ICreditNoteDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                if (companyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                var rows = await repository.ListAsync(companyId, includeDrafts ?? true, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapGet(
            "/vendor-credits",
            async (CompanyId companyId, bool? includeDrafts, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                if (companyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                var rows = await repository.ListAsync(companyId, includeDrafts ?? true, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapGet(
            "/bank-transfers",
            async (CompanyId companyId, bool? includeDrafts, IBankTransferDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                if (companyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                var rows = await repository.ListAsync(companyId, includeDrafts ?? true, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapGet(
            "/bank-deposits",
            async (CompanyId companyId, bool? includeDrafts, IBankDepositDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                if (companyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                var rows = await repository.ListAsync(companyId, includeDrafts ?? true, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapGet(
            "/tax-returns",
            async (CompanyId companyId, bool? includeDrafts, ITaxReturnDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                if (companyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                var rows = await repository.ListAsync(companyId, includeDrafts ?? true, cancellationToken);
                return Results.Ok(rows);
            });

        // ============================================================================
        // V1 detail endpoints for the 7 doc types that now post end-to-end.
        // Each endpoint just exposes the existing repository's
        // GetForPostingAsync — no new persistence shape, no new SQL.
        // Frontend Detail pages call these to render a posted document.
        // ============================================================================

        accounting.MapGet(
            "/sales-receipts/{documentId:guid}",
            async (Guid documentId, [AsParameters] V1PendingLookupQuery query, ISalesReceiptDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(query.CompanyId, documentId, cancellationToken);
                if (document is null)
                {
                    return Results.NotFound(new { message = "Sales receipt not found in the active company context." });
                }
                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    ReceiptDate = document.DocumentDate,
                    CustomerId = document.CustomerId,
                    DepositToAccountId = document.DepositToAccountId,
                    document.PaymentMethod,
                    document.ReferenceNo,
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    FxRate = document.FxSnapshot?.Rate,
                    document.SubtotalAmount,
                    document.TaxAmount,
                    document.TotalAmount,
                    document.Memo,
                    document.CustomerPoNumber,
                    Lines = document.ReceiptLines.Select(line => new
                    {
                        line.LineNumber,
                        line.RevenueAccountId,
                        line.Description,
                        line.Quantity,
                        line.UnitPrice,
                        line.LineAmount,
                        line.TaxAmount,
                        line.TaxCodeId,
                        line.PayableTaxAccountId,
                    })
                });
            });

        accounting.MapGet(
            "/refund-receipts/{documentId:guid}",
            async (Guid documentId, [AsParameters] V1PendingLookupQuery query, IRefundReceiptDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(query.CompanyId, documentId, cancellationToken);
                if (document is null)
                {
                    return Results.NotFound(new { message = "Refund receipt not found in the active company context." });
                }
                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    RefundDate = document.DocumentDate,
                    CustomerId = document.CustomerId,
                    RefundFromAccountId = document.RefundFromAccountId,
                    document.PaymentMethod,
                    document.ReferenceNo,
                    document.Reason,
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    FxRate = document.FxSnapshot?.Rate,
                    document.SubtotalAmount,
                    document.TaxAmount,
                    document.TotalAmount,
                    document.Memo,
                    document.CustomerPoNumber,
                    Lines = document.ReceiptLines.Select(line => new
                    {
                        line.LineNumber,
                        line.RevenueAccountId,
                        line.Description,
                        line.Quantity,
                        line.UnitPrice,
                        line.LineAmount,
                        line.TaxAmount,
                        line.TaxCodeId,
                        line.PayableTaxAccountId,
                    })
                });
            });

        accounting.MapGet(
            "/bank-transfers/{documentId:guid}",
            async (Guid documentId, [AsParameters] V1PendingLookupQuery query, IBankTransferDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(query.CompanyId, documentId, cancellationToken);
                if (document is null)
                {
                    return Results.NotFound(new { message = "Bank transfer not found in the active company context." });
                }
                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    TransferDate = document.DocumentDate,
                    FromAccountId = document.FromAccountId,
                    FromCurrencyCode = document.FromCurrencyCode.Value,
                    ToAccountId = document.ToAccountId,
                    ToCurrencyCode = document.ToCurrencyCode.Value,
                    document.Amount,
                    document.FxRate,
                    document.ReferenceNo,
                    document.Memo,
                });
            });

        accounting.MapGet(
            "/bank-deposits/{documentId:guid}",
            async (Guid documentId, [AsParameters] V1PendingLookupQuery query, IBankDepositDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(query.CompanyId, documentId, cancellationToken);
                if (document is null)
                {
                    return Results.NotFound(new { message = "Bank deposit not found in the active company context." });
                }
                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    DepositDate = document.DocumentDate,
                    DepositToAccountId = document.DepositToAccountId,
                    UndepositedFundsAccountId = document.UndepositedFundsAccountId,
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    document.TotalAmount,
                    document.ReferenceNo,
                    document.Memo,
                    Items = document.Items.Select(item => new
                    {
                        item.LineNumber,
                        item.SourceItemKind,
                        item.SourceItemId,
                        item.SourceItemDisplayNumber,
                        item.PayerName,
                        item.PaymentMethod,
                        item.ReferenceNo,
                        item.Amount,
                    })
                });
            });

        accounting.MapGet(
            "/tax-returns/{documentId:guid}",
            async (Guid documentId, [AsParameters] V1PendingLookupQuery query, ITaxReturnDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(query.CompanyId, documentId, cancellationToken);
                if (document is null)
                {
                    return Results.NotFound(new { message = "Tax return not found in the active company context." });
                }
                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    document.TaxRegime,
                    document.FilingFrequency,
                    document.PeriodStart,
                    document.PeriodEnd,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    document.CollectedAmount,
                    document.InputCreditsAmount,
                    document.AdjustmentsAmount,
                    document.AdjustmentsNote,
                    document.NetAmount,
                    document.RegulatorReferenceNo,
                    document.Memo,
                });
            });
    }
}
