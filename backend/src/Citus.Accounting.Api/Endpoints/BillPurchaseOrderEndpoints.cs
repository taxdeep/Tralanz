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
/// BillPurchaseOrder endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class BillPurchaseOrderEndpoints
{
    public static void MapBillPurchaseOrderEndpoints(this RouteGroupBuilder accounting)
    {

        accounting.MapGet(
            "/bills/drafts/{documentId:guid}",
            async (Guid documentId, [AsParameters] BillLookupQuery query, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(query.CompanyId, documentId, cancellationToken);
                return document is null || (document.Status != "draft" && document.Status != "submitted")
                    ? Results.NotFound(new { message = "Bill draft or submitted bill was not found in the active company context." })
                    : Results.Ok(new
                    {
                        document.Id,
                        CompanyId = document.CompanyId,
                        EntityNumber = document.EntityNumber.Value,
                        DisplayNumber = document.DisplayNumber.Value,
                        document.Status,
                        VendorId = document.PartyId,
                        DocumentDate = document.DocumentDate,
                        DueDate = document.DueDate,
                        TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                        BaseCurrencyCode = document.BaseCurrencyCode.Value,
                        FxSnapshotId = document.FxSnapshot?.SnapshotId,
                        FxRate = document.FxSnapshot?.Rate,
                        FxEffectiveDate = document.FxSnapshot?.EffectiveDate,
                        FxSource = document.FxSnapshot?.SourceSemantics,
                        document.Memo,
                        Lines = document.BillLines.Select(line => new
                        {
                            line.LineNumber,
                            line.ExpenseAccountId,
                            line.Description,
                            line.LineAmount,
                            line.TaxCodeId,
                            line.TaxAmount,
                            line.IsTaxRecoverable,
                            line.ItemId,
                            line.WarehouseId,
                            line.UomCode,
                            line.Quantity,
                            line.UnitCost,
                            line.PurchaseOrderId,
                            line.PurchaseOrderLineNumber
                        })
                    });
            });

        accounting.MapPost(
            "/bills/drafts",
            async (SaveBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.SaveDraftAsync(
                        new BillDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.BillDate,
                            request.DueDate,
                            request.TransactionCurrencyCode,
                            request.BaseCurrencyCode,
                            request.FxSnapshotId,
                            request.FxRate,
                            request.FxEffectiveDate,
                            request.FxSource,
                            request.Memo,
                            request.Lines.Select(static line => new BillDraftLineSaveModel(
                                line.LineNumber,
                                line.ExpenseAccountId,
                                line.Description,
                                line.LineAmount,
                                line.TaxCodeId,
                                line.TaxAmount,
                                line.IsTaxRecoverable,
                                line.ItemId,
                                line.WarehouseId,
                                line.UomCode,
                                line.Quantity,
                                line.UnitCost,
                                line.PurchaseOrderId,
                                line.PurchaseOrderLineNumber,
                                line.TaxCodeSetId,
                                line.TaskId)).ToArray(),
                            request.PaymentTermId,
                            request.SourcePurchaseOrderId,
                            request.SourcePurchaseOrderNumber,
                            request.BillNumber),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPut(
            "/bills/drafts/{documentId:guid}",
            async (Guid documentId, SaveBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.SaveDraftAsync(
                        new BillDraftSaveModel(
                            documentId,
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.BillDate,
                            request.DueDate,
                            request.TransactionCurrencyCode,
                            request.BaseCurrencyCode,
                            request.FxSnapshotId,
                            request.FxRate,
                            request.FxEffectiveDate,
                            request.FxSource,
                            request.Memo,
                            request.Lines.Select(static line => new BillDraftLineSaveModel(
                                line.LineNumber,
                                line.ExpenseAccountId,
                                line.Description,
                                line.LineAmount,
                                line.TaxCodeId,
                                line.TaxAmount,
                                line.IsTaxRecoverable,
                                line.ItemId,
                                line.WarehouseId,
                                line.UomCode,
                                line.Quantity,
                                line.UnitCost,
                                line.PurchaseOrderId,
                                line.PurchaseOrderLineNumber,
                                line.TaxCodeSetId,
                                line.TaskId)).ToArray(),
                            request.PaymentTermId,
                            request.SourcePurchaseOrderId,
                            request.SourcePurchaseOrderNumber,
                            request.BillNumber),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/bills/drafts/{documentId:guid}/submit",
            async (Guid documentId, SubmitBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
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

        accounting.MapPost(
            "/bills/drafts/{documentId:guid}/cancel",
            async (Guid documentId, SubmitBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.CancelSubmittedAsync(
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
            "/bills/{documentId:guid}",
            async (
                Guid documentId,
                [AsParameters] BillLookupQuery query,
                IBillDocumentRepository repository,
                IReceiptGrIrApSettlementControlStore grIrSettlementStore,
                CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                if (document is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Bill document was not found in the active company context."
                    });
                }

                var grIrSettlementSummary = await grIrSettlementStore.GetBillSettlementSummaryAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    document.DocumentDate,
                    document.DueDate,
                    VendorId = document.PartyId,
                    PayableAccountId = document.PayableAccountId,
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    document.SubtotalAmount,
                    document.TaxAmount,
                    document.TotalAmount,
                    document.Memo,
                    GrIrSettlement = grIrSettlementSummary is null
                        ? null
                        : new
                        {
                            grIrSettlementSummary.SettlementStatus,
                            grIrSettlementSummary.SettlementLineCount,
                            grIrSettlementSummary.EligibleLineCount,
                            grIrSettlementSummary.BlockedLineCount,
                            grIrSettlementSummary.BlockedGrIrNotPostedLineCount,
                            grIrSettlementSummary.BlockedBillNotPostedLineCount,
                            grIrSettlementSummary.BlockedMissingApOpenItemLineCount,
                            grIrSettlementSummary.BlockedJournalNotPostedLineCount,
                            grIrSettlementSummary.BlockedAmountExceededLineCount,
                            grIrSettlementSummary.PartiallySettledLineCount,
                            grIrSettlementSummary.SettledLineCount,
                            grIrSettlementSummary.SettlementAmountBase,
                            grIrSettlementSummary.EligibleAmountBase,
                            grIrSettlementSummary.SettledAmountBase,
                            grIrSettlementSummary.RemainingAmountBase,
                            grIrSettlementSummary.SettlementBatchCount,
                            grIrSettlementSummary.JournalNotPostedBatchCount,
                            grIrSettlementSummary.JournalPostedBatchCount,
                            grIrSettlementSummary.JournalStaleBatchCount,
                            grIrSettlementSummary.JournalInconsistentBatchCount,
                            grIrSettlementSummary.JournalReconciliationStatus,
                            grIrSettlementSummary.LastJournalRefreshedAt,
                            grIrSettlementSummary.OpenItemNotClearedBatchCount,
                            grIrSettlementSummary.OpenItemClearedBatchCount,
                            grIrSettlementSummary.OpenItemReversedBatchCount,
                            grIrSettlementSummary.OpenItemBlockedBatchCount,
                            grIrSettlementSummary.OpenItemStaleBatchCount,
                            grIrSettlementSummary.OpenItemInconsistentBatchCount,
                            grIrSettlementSummary.OpenItemClearingStatus,
                            grIrSettlementSummary.LastOpenItemClearedAt,
                            grIrSettlementSummary.LastOpenItemReversedAt,
                            grIrSettlementSummary.PurchaseVarianceLineCount,
                            grIrSettlementSummary.PurchaseVarianceCandidateLineCount,
                            grIrSettlementSummary.PurchaseVarianceNoVarianceLineCount,
                            grIrSettlementSummary.PurchaseVarianceBlockedLineCount,
                            grIrSettlementSummary.PurchaseVarianceStatus,
                            grIrSettlementSummary.PurchaseVarianceAmountBase,
                            grIrSettlementSummary.LastPurchaseVarianceRefreshedAt,
                            grIrSettlementSummary.LastRefreshedAt,
                            grIrSettlementSummary.LastSettledAt
                        },
                    Lines = document.BillLines.Select(line => new
                    {
                        line.LineNumber,
                        line.ExpenseAccountId,
                        line.Description,
                        line.LineAmount,
                        line.TaxAmount,
                        line.IsTaxRecoverable,
                        line.RecoverableTaxAccountId,
                        line.ItemId,
                        line.WarehouseId,
                        line.UomCode,
                        line.Quantity,
                        line.UnitCost,
                        line.PurchaseOrderId,
                        line.PurchaseOrderLineNumber
                    })
                });
            });

        accounting.MapGet(
            "/bills/{documentId:guid}/receipt-matching",
            async (Guid documentId, [AsParameters] BillLookupQuery query, IBillReceiptMatchingRepository repository, CancellationToken cancellationToken) =>
            {
                var summary = await repository.GetBillLaneSummaryAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                return Results.Ok(new
                {
                    summary.BillDocumentId,
                    summary.BillInboundLineCount,
                    summary.BillInboundQuantity,
                    summary.ReceiptCount,
                    summary.CoveredQuantity,
                    summary.RemainingQuantity,
                    summary.MatchStatus,
                    summary.LatestReceiptPostedAt,
                    OpenDiscrepancyCount = summary.Discrepancies.Count,
                    RecentReceipts = summary.RecentReceipts.Select(receipt => new
                    {
                        receipt.ReceiptDocumentId,
                        receipt.DisplayNumber,
                        receipt.ReceiptDate,
                        receipt.Status,
                        receipt.ReceiptQuantity,
                        receipt.MatchedQuantity,
                        receipt.VendorReference,
                        receipt.SourceReference,
                        receipt.PostedAt
                    }),
                    LineSummaries = summary.LineSummaries.Select(line => new
                    {
                        line.BillLineNumber,
                        line.ItemId,
                        line.ItemCode,
                        line.ItemName,
                        line.WarehouseId,
                        line.WarehouseCode,
                        line.WarehouseName,
                        line.UomCode,
                        line.BillQuantity,
                        line.CoveredQuantity,
                        line.RemainingQuantity,
                        line.ReceiptCount,
                        line.MatchStatus
                    }),
                    Discrepancies = summary.Discrepancies.Select(discrepancy => new
                    {
                        discrepancy.BillDocumentId,
                        discrepancy.BillLineNumber,
                        discrepancy.DiscrepancyType,
                        discrepancy.InvestigationStatus,
                        discrepancy.ItemId,
                        discrepancy.ItemCode,
                        discrepancy.ItemName,
                        discrepancy.WarehouseId,
                        discrepancy.WarehouseCode,
                        discrepancy.WarehouseName,
                        discrepancy.UomCode,
                        discrepancy.BillQuantity,
                        discrepancy.CoveredQuantity,
                        discrepancy.RemainingQuantity,
                        discrepancy.Summary,
                        discrepancy.FirstDetectedAt,
                        discrepancy.LastDetectedAt
                    })
                });
            });

        accounting.MapPost(
            "/bills/{documentId:guid}/post",
            async (Guid documentId, PostBillHttpRequest request, PostBillCommandHandler handler, IUnitySearchProjectionStore unitySearchProjectionStore, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostBillCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-b: bill status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApBillPost);

        // Reverse a posted bill: posts a compensating JE (source_type='bill_reversal')
        // that flips every original leg incl. each per-rule recoverable-tax (ITC) leg,
        // then flips the bill to 'reversed'. Mirror of /invoices/{id}/reverse.
        accounting.MapPost(
            "/bills/{documentId:guid}/reverse",
            async (
                Guid documentId,
                BusinessSessionContextAccessor sessionAccessor,
                PostBillReverseCommandHandler reverseHandler,
                IBillDocumentRepository repository,
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
                    var result = await reverseHandler.HandleAsync(
                        new PostBillReverseCommand(session.ActiveCompanyId, session.UserId, documentId),
                        cancellationToken);

                    // Flip the bill out of the payable set (mirrors the invoice
                    // reverse: compensation JE first, then the source-row status flip).
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApBillPost);

        accounting.MapGet(
            "/purchase-orders",
            async (
                [AsParameters] PurchaseOrderListQuery query,
                IPurchaseOrderDocumentRepository repository,
                CancellationToken cancellationToken) =>
            {
                var documents = await repository.ListAsync(query.CompanyId, query.Take ?? 50, cancellationToken);
                var summaries = await repository.GetThreeQuantitySummariesAsync(
                    query.CompanyId,
                    documents.Select(static document => document.DocumentId).ToArray(),
                    cancellationToken);

                return Results.Ok(documents.Select(document => new
                {
                    EstimatedAmount = CalculatePurchaseOrderListEstimatedAmount(document),
                    document.DocumentId,
                    document.EntityNumber,
                    document.DisplayNumber,
                    document.Status,
                    document.VendorId,
                    document.OrderDate,
                    document.ExpectedDate,
                    document.LineCount,
                    document.TotalOrderedQuantity,
                    document.VendorReference,
                    document.Memo,
                    document.CreatedAt,
                    document.UpdatedAt,
                    document.ApprovedAt,
                    document.IssuedAt,
                    document.ClosedAt,
                    document.CancelledAt,
                    document.AmendmentStartedAt,
                    AnchorGovernance = new
                    {
                        AllowsNewAnchors = PurchaseOrderAnchorPolicy.AllowsNewAnchor(document.Status),
                        Summary = PurchaseOrderAnchorPolicy.BuildAnchorStatusSummary(document.Status)
                    },
                    ApprovalAuthority = BuildPurchaseOrderApprovalAuthoritySummary(CalculatePurchaseOrderListEstimatedAmount(document)),
                    ThreeQuantity = summaries.TryGetValue(document.DocumentId, out var summary) ? summary : null
                }));
            });

        accounting.MapGet(
            "/purchase-orders/approval-requests",
            async (
                [AsParameters] PurchaseOrderApprovalRequestListQuery query,
                IPurchaseOrderDocumentRepository repository,
                CancellationToken cancellationToken) =>
            {
                var requests = await repository.ListApprovalRequestsAsync(
                    query.CompanyId,
                    query.Take ?? 50,
                    query.IncludeClosed ?? false,
                    cancellationToken);

                return Results.Ok(requests);
            });

        accounting.MapGet(
            "/purchase-orders/{documentId:guid}",
            async (
                Guid documentId,
                [AsParameters] PurchaseOrderLookupQuery query,
                IPurchaseOrderDocumentRepository repository,
                CancellationToken cancellationToken) =>
            {
                var document = await repository.GetAsync(query.CompanyId, documentId, cancellationToken);
                if (document is null)
                {
                    return Results.NotFound(new { message = "Purchase order document was not found in the active company context." });
                }

                var summary = await repository.GetThreeQuantitySummaryAsync(query.CompanyId, documentId, cancellationToken);
                var purchaseVarianceSummary = await repository.GetPurchaseVarianceSummaryAsync(query.CompanyId, documentId, cancellationToken);
                var estimatedAmount = CalculatePurchaseOrderDocumentEstimatedAmount(document);
                return Results.Ok(new
                {
                    EstimatedAmount = estimatedAmount,
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    document.VendorId,
                    document.OrderDate,
                    document.ExpectedDate,
                    document.VendorReference,
                    document.Memo,
                    document.ApprovedAt,
                    document.IssuedAt,
                    document.ClosedAt,
                    document.CancelledAt,
                    document.AmendmentStartedAt,
                    AnchorGovernance = new
                    {
                        AllowsNewAnchors = PurchaseOrderAnchorPolicy.AllowsNewAnchor(document.Status),
                        Summary = PurchaseOrderAnchorPolicy.BuildAnchorStatusSummary(document.Status)
                    },
                    ApprovalAuthority = BuildPurchaseOrderApprovalAuthoritySummary(estimatedAmount),
                    ThreeQuantity = summary,
                    PurchaseVariance = purchaseVarianceSummary,
                    Lines = document.PurchaseOrderLines.Select(line => new
                    {
                        line.LineNumber,
                        line.ItemId,
                        line.OrderedQuantity,
                        line.UomCode,
                        line.Description,
                        line.UnitCost
                    })
                });
            });

        accounting.MapGet(
            "/purchase-orders/{documentId:guid}/lifecycle-audit",
            async (
                Guid documentId,
                [AsParameters] PurchaseOrderLifecycleAuditQuery query,
                IPurchaseOrderDocumentRepository repository,
                CancellationToken cancellationToken) =>
            {
                var document = await repository.GetAsync(query.CompanyId, documentId, cancellationToken);
                if (document is null)
                {
                    return Results.NotFound(new { message = "Purchase order document was not found in the active company context." });
                }

                var entries = await repository.ListLifecycleAuditAsync(
                    query.CompanyId,
                    documentId,
                    query.Take ?? 50,
                    cancellationToken);

                return Results.Ok(entries);
            });

        accounting.MapGet(
            "/purchase-orders/{documentId:guid}/approval-request",
            async (
                Guid documentId,
                [AsParameters] PurchaseOrderLookupQuery query,
                IPurchaseOrderDocumentRepository repository,
                CancellationToken cancellationToken) =>
            {
                var request = await repository.GetLatestApprovalRequestAsync(query.CompanyId, documentId, cancellationToken);
                return request is null
                    ? Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." })
                    : Results.Ok(request);
            });

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/approval-request",
            async (
                Guid documentId,
                RequestPurchaseOrderApprovalHttpRequest request,
                IPurchaseOrderDocumentRepository repository,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.RequestApprovalAsync(
                        request.CompanyId,
                        request.UserId,
                        documentId,
                        request.Reason,
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/approval-request/{requestId:guid}/submit",
            async (
                Guid documentId,
                Guid requestId,
                SubmitPurchaseOrderApprovalRequestHttpRequest request,
                IPurchaseOrderDocumentRepository repository,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.SubmitApprovalRequestAsync(
                        request.CompanyId,
                        request.UserId,
                        documentId,
                        requestId,
                        cancellationToken);

                    return result is null
                        ? Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." })
                        : Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/approval-request/{requestId:guid}/reject",
            async (
                Guid documentId,
                Guid requestId,
                RejectPurchaseOrderApprovalRequestHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IPurchaseOrderDocumentRepository repository,
                CancellationToken cancellationToken) =>
            {
                var current = await repository.GetLatestApprovalRequestAsync(request.CompanyId, documentId, cancellationToken);
                if (current is null || current.RequestId != requestId)
                {
                    return Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." });
                }

                var authorityBlock = RequirePurchaseOrderApprovalAuthority(
                    sessionAccessor.Current,
                    "reject_approval_request",
                    current.EstimatedAmount);
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await repository.RejectApprovalRequestAsync(
                        request.CompanyId,
                        request.UserId,
                        documentId,
                        requestId,
                        cancellationToken);

                    return result is null
                        ? Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." })
                        : Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/purchase-orders/drafts",
            async (SavePurchaseOrderDraftHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.SaveDraftAsync(
                        new PurchaseOrderDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.OrderDate,
                            request.ExpectedDate,
                            request.VendorReference,
                            request.Memo,
                            request.Lines.Select(static line => new PurchaseOrderDraftLineSaveModel(
                                line.LineNumber,
                                line.ItemId,
                                line.OrderedQuantity,
                                line.UomCode,
                                line.Description,
                                line.UnitCost)).ToArray()),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPut(
            "/purchase-orders/drafts/{documentId:guid}",
            async (Guid documentId, SavePurchaseOrderDraftHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.SaveDraftAsync(
                        new PurchaseOrderDraftSaveModel(
                            documentId,
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.OrderDate,
                            request.ExpectedDate,
                            request.VendorReference,
                            request.Memo,
                            request.Lines.Select(static line => new PurchaseOrderDraftLineSaveModel(
                                line.LineNumber,
                                line.ItemId,
                                line.OrderedQuantity,
                                line.UomCode,
                                line.Description,
                                line.UnitCost)).ToArray()),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/approve",
            async (Guid documentId, ApprovePurchaseOrderHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetAsync(request.CompanyId, documentId, cancellationToken);
                if (document is null)
                {
                    return Results.NotFound(new { message = "Purchase order document was not found in the active company context." });
                }

                var authorityBlock = RequirePurchaseOrderApprovalAuthority(
                    sessionAccessor.Current,
                    "approve",
                    CalculatePurchaseOrderDocumentEstimatedAmount(document));
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await repository.ApproveAsync(
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

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/approval/reverse",
            async (
                Guid documentId,
                ReversePurchaseOrderApprovalHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IPurchaseOrderDocumentRepository repository,
                CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequirePurchaseOrderApprovalReversalAuthority(
                    sessionAccessor.Current,
                    "reverse_approval");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await repository.ReverseApprovalAsync(
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

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/issue",
            async (Guid documentId, IssuePurchaseOrderHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequirePurchaseOrderReleaseAuthority(sessionAccessor.Current, "release");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await repository.IssueAsync(
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

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/reopen-for-amendment",
            async (Guid documentId, ReopenPurchaseOrderForAmendmentHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequirePurchaseOrderAmendmentAuthority(sessionAccessor.Current, "reopen_for_amendment");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await repository.ReopenForAmendmentAsync(
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

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/close",
            async (Guid documentId, ClosePurchaseOrderHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequirePurchaseOrderCloseAuthority(sessionAccessor.Current, "close");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await repository.CloseAsync(
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

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/cancel",
            async (Guid documentId, CancelPurchaseOrderHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequirePurchaseOrderCancelAuthority(sessionAccessor.Current, "cancel");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await repository.CancelAsync(
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

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/quantity-discrepancies/refresh",
            async (Guid documentId, RefreshPurchaseOrderQuantityDiscrepanciesHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var summary = await repository.RefreshQuantityDiscrepanciesAsync(
                        request.CompanyId,
                        request.UserId,
                        documentId,
                        cancellationToken);

                    return summary is null
                        ? Results.NotFound(new { message = "Purchase order document was not found in the active company context." })
                        : Results.Ok(summary);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/purchase-orders/{documentId:guid}/quantity-discrepancies/review",
            async (Guid documentId, ReviewPurchaseOrderQuantityDiscrepancyHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var summary = await repository.ReviewQuantityDiscrepancyAsync(
                        request.CompanyId,
                        request.UserId,
                        documentId,
                        request.PurchaseOrderLineNumber,
                        request.DiscrepancyType,
                        request.InvestigationStatus,
                        request.ReviewNote,
                        cancellationToken);

                    return summary is null
                        ? Results.NotFound(new { message = "Purchase order document was not found in the active company context." })
                        : Results.Ok(summary);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { code = "invalid_operation", message = ex.Message });
                }
            });
    }
}
