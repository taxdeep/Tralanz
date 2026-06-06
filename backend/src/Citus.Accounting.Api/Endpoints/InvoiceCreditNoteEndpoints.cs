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
/// InvoiceCreditNote endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class InvoiceCreditNoteEndpoints
{
    public static void MapInvoiceCreditNoteEndpoints(this RouteGroupBuilder accounting)
    {

        accounting.MapGet(
            "/invoices/drafts/{documentId:guid}",
            async (Guid documentId, [AsParameters] InvoiceLookupQuery query, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                return document is null || (document.Status != "draft" && document.Status != "submitted")
                    ? Results.NotFound(new { message = "Invoice draft or submitted invoice was not found in the active company context." })
                    : Results.Ok(new
                    {
                        document.Id,
                        CompanyId = document.CompanyId,
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

        // Read-only preview of the next auto invoice number, so the create page can
        // pre-fill the editable "Invoice #" field with the system default.
        accounting.MapGet(
            "/invoices/next-number",
            async (CompanyId companyId, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                if (companyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                var nextNumber = await repository.PeekNextDisplayNumberAsync(companyId, cancellationToken);
                return Results.Ok(new { nextNumber });
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
                            request.CompanyId,
                            request.UserId,
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
                                line.UomCode,
                                line.TaskId,
                                line.TaskLineId,
                                line.TaxCodeSetId)).ToArray(),
                            string.IsNullOrWhiteSpace(request.CustomerPoNumber) ? null : request.CustomerPoNumber.Trim(),
                            request.SalesOrderId,
                            InvoiceNumber: request.InvoiceNumber,
                            BillingAddress: request.BillingAddress,
                            ShippingAddress: request.ShippingAddress),
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
                            request.CompanyId,
                            request.UserId,
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
                                line.UomCode,
                                line.TaskId,
                                line.TaskLineId,
                                line.TaxCodeSetId)).ToArray(),
                            string.IsNullOrWhiteSpace(request.CustomerPoNumber) ? null : request.CustomerPoNumber.Trim(),
                            request.SalesOrderId,
                            request.ExpectedUpdatedAt,
                            InvoiceNumber: request.InvoiceNumber,
                            BillingAddress: request.BillingAddress,
                            ShippingAddress: request.ShippingAddress),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (ConcurrencyConflictException ex)
                {
                    // 409 Conflict — the draft moved between the editor's GET
                    // and this PUT. Front-end catches this and prompts the
                    // operator to refresh + re-apply, instead of silently
                    // overwriting the other session's changes.
                    return Results.Conflict(new
                    {
                        code = "concurrency_conflict",
                        message = ex.Message,
                    });
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

        accounting.MapGet(
            "/invoices/{documentId:guid}",
            async (Guid documentId, [AsParameters] InvoiceLookupQuery query, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(
                    query.CompanyId,
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
                    CompanyId = document.CompanyId,
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
                    document.CustomerPoNumber,
                    document.SalesOrderId,
                    document.BillingAddress,
                    document.ShippingAddress,
                    Lines = document.InvoiceLines.Select(line => new
                    {
                        line.LineNumber,
                        line.RevenueAccountId,
                        line.Description,
                        line.Quantity,
                        line.UnitPrice,
                        line.LineAmount,
                        line.TaxAmount,
                        line.PayableTaxAccountId,
                        line.TaskId
                    }),
                    // Per-Tax-Rule tax breakdown (GST, PST-BC, …) aggregated from the
                    // line snapshots so the detail Totals can split a multi-rule Tax
                    // Code into its component rules.
                    TaxBreakdown = document.InvoiceLines
                        .SelectMany(line => line.TaxSnapshots)
                        .GroupBy(snapshot => snapshot.Code)
                        .OrderBy(group => group.Min(snapshot => snapshot.Sequence))
                        .Select(group => new { Code = group.Key, Amount = group.Sum(snapshot => snapshot.TaxAmount) })
                });
            });

        accounting.MapPost(
            "/invoices/{documentId:guid}/post",
            async (Guid documentId, PostInvoiceHttpRequest request, PostInvoiceCommandHandler handler, IUnitySearchProjectionStore unitySearchProjectionStore, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostInvoiceCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-b: invoice status flipped draft → posted; the projection's
                    // status filter needs to refresh so the invoice surfaces in the
                    // topbar search + AR pickers without the 5-min refresh wait.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArInvoicePost);

        accounting.MapPost(
            "/invoices/{documentId:guid}/reverse",
            async (
                Guid documentId,
                BusinessSessionContextAccessor sessionAccessor,
                PostInvoiceReverseCommandHandler reverseHandler,
                IInvoiceDocumentRepository repository,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
                {
                    return Results.Unauthorized();
                }
                if (string.IsNullOrEmpty(session.UserId.Value))
                {
                    return Results.Unauthorized();
                }

                try
                {
                    // Post the compensating JE through the posting engine — reverses
                    // every original leg incl. each per-rule sales-tax leg. Idempotent
                    // on source_type='invoice_reversal' + source_id.
                    var result = await reverseHandler.HandleAsync(
                        new PostInvoiceReverseCommand(session.ActiveCompanyId, session.UserId, documentId),
                        cancellationToken);

                    // Flip the invoice out of the receivable set (mirrors the expense
                    // void: compensation JE first, then the source-row status flip).
                    await repository.MarkReversedAsync(session.ActiveCompanyId, documentId, cancellationToken);

                    await unitySearchProjectionStore.InvalidateAsync(session.ActiveCompanyId, cancellationToken);
                    return Results.Ok(new
                    {
                        reversed = true,
                        compensationJournalEntryId = result.JournalEntryId,
                        compensationDisplayNumber = result.JournalEntryDisplayNumber,
                        alreadyReversed = result.AlreadyReversed,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArInvoicePost);

        accounting.MapGet(
            "/invoices",
            async (CompanyId companyId, bool? includeDrafts, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                if (companyId.Value is null) return Results.BadRequest(new { error = "companyId required" });
                var rows = await repository.ListAsync(companyId, includeDrafts ?? true, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapGet(
            "/credit-notes/drafts/{documentId:guid}",
            async (Guid documentId, [AsParameters] CreditNoteLookupQuery query, ICreditNoteDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(query.CompanyId, documentId, cancellationToken);
                return document is null || document.Status != "draft"
                    ? Results.NotFound(new { message = "Credit note draft was not found in the active company context." })
                    : Results.Ok(new
                    {
                        document.Id,
                        CompanyId = document.CompanyId,
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
                            request.CompanyId,
                            request.UserId,
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
                                line.TaxAmount,
                                line.TaskId,
                                line.TaskLineId)).ToArray()),
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
                            request.CompanyId,
                            request.UserId,
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
                                line.TaxAmount,
                                line.TaskId,
                                line.TaskLineId)).ToArray()),
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
                    query.CompanyId,
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
                    CompanyId = document.CompanyId,
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
            async (Guid documentId, PostCreditNoteHttpRequest request, PostCreditNoteCommandHandler handler, IUnitySearchProjectionStore unitySearchProjectionStore, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostCreditNoteCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-b: credit-note status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });
    }
}
