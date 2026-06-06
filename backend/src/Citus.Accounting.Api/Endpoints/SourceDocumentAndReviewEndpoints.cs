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
/// SourceDocumentAndReview endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class SourceDocumentAndReviewEndpoints
{
    public static void MapSourceDocumentAndReviewEndpoints(this RouteGroupBuilder accounting)
    {

        accounting.MapGet(
            "/documents/source",
            async (
                [AsParameters] SourceDocumentBrowserLookupQuery query,
                IAccountingDocumentReviewRepository repository,
                IBillReceiptMatchingRepository billReceiptMatchingRepository,
                IInventoryShipmentStore inventoryShipmentStore,
                CancellationToken cancellationToken) =>
            {
                var items = await repository.ListSourceDocumentsAsync(
                    query.CompanyId,
                    query.SourceType,
                    query.CounterpartyRole,
                    query.CounterpartyId,
                    query.Limit ?? 100,
                    cancellationToken);

                var billIds = items
                    .Where(static item => string.Equals(item.SourceType, "bill", StringComparison.OrdinalIgnoreCase))
                    .Select(static item => item.Id)
                    .Distinct()
                    .ToArray();
                var billReceiptSummaries = await billReceiptMatchingRepository.GetBillPostingGateSnapshotsAsync(
                    query.CompanyId,
                    billIds,
                    cancellationToken);
                var invoiceIds = items
                    .Where(static item => string.Equals(item.SourceType, "invoice", StringComparison.OrdinalIgnoreCase))
                    .Select(static item => item.Id)
                    .Distinct()
                    .ToArray();
                var invoiceShipmentSummaries = await inventoryShipmentStore.GetInvoicePostingGateSnapshotsAsync(
                    query.CompanyId,
                    invoiceIds,
                    cancellationToken);

                return Results.Ok(items.Select(item =>
                {
                    billReceiptSummaries.TryGetValue(item.Id, out var receiptSummary);
                    invoiceShipmentSummaries.TryGetValue(item.Id, out var shipmentSummary);
                    return new
                    {
                        item.SourceType,
                        SourceTypeLabel = MapDocumentReviewSourceLabel(item.SourceType),
                        item.Id,
                        CompanyId = item.CompanyId,
                        item.EntityNumber,
                        item.DisplayNumber,
                        item.Status,
                        item.DocumentDate,
                        item.DueDate,
                        CounterpartyLabel = MapDocumentReviewCounterpartyLabel(item.CounterpartyRole),
                        item.CounterpartyId,
                        item.CounterpartyDisplayName,
                        item.TransactionCurrencyCode,
                        item.BaseCurrencyCode,
                        item.TotalAmount,
                        item.JournalEntryId,
                        item.JournalEntryDisplayNumber,
                        item.JournalEntryStatus,
                        item.JournalEntryPostedAt,
                        item.JournalEntryVoidedAt,
                        item.JournalEntryReversedAt,
                        BillReceiptMatchStatus = receiptSummary?.MatchStatus,
                        BillReceiptPostingGateLabel = receiptSummary is null ? null : BillReceiptPostingGate.GetPostingGateLabel(receiptSummary),
                        BillReceiptPostingGateSummary = receiptSummary is null ? null : BillReceiptPostingGate.GetPostingGateSummary(receiptSummary),
                        BillReceiptAllowsPost = receiptSummary is null ? (bool?)null : BillReceiptPostingGate.AllowsBillPost(receiptSummary.MatchStatus),
                        BillReceiptOpenDiscrepancyCount = receiptSummary?.OpenDiscrepancyCount,
                        BillReceiptInvestigationSummary = receiptSummary is null ? null : BillReceiptDiscrepancyPolicy.BuildBrowserSummary(receiptSummary.OpenDiscrepancyCount),
                        InvoiceShipmentMatchStatus = shipmentSummary?.MatchStatus,
                        InvoiceShipmentPostingGateLabel = shipmentSummary is null ? null : ShipmentPostingGatePolicy.GetPostingGateLabel(shipmentSummary),
                        InvoiceShipmentPostingGateSummary = shipmentSummary is null ? null : ShipmentPostingGatePolicy.GetPostingGateSummary(shipmentSummary),
                        InvoiceShipmentAllowsPost = shipmentSummary is null ? (bool?)null : ShipmentPostingGatePolicy.AllowsInvoicePost(shipmentSummary.MatchStatus),
                        InvoiceCoverageStatus = shipmentSummary?.InvoiceCoverageStatus,
                        InvoiceCoverageSummary = shipmentSummary is null ? null : BuildInvoiceCoverageSummary(shipmentSummary)
                    };
                }));
            });

        accounting.MapGet(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}",
            async (
                string sourceType,
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var preview = await repository.GetLifecyclePreviewAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    cancellationToken);

                if (preview is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Source document lifecycle preview was not found in the active company context."
                    });
                }

                return Results.Ok(new
                {
                    preview.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(preview.SourceType),
                    preview.Id,
                    CompanyId = preview.CompanyId,
                    preview.EntityNumber,
                    preview.DisplayNumber,
                    preview.Status,
                    preview.JournalEntryId,
                    preview.JournalEntryDisplayNumber,
                    preview.JournalEntryStatus,
                    preview.JournalEntryPostedAt,
                    preview.JournalEntryVoidedAt,
                    preview.JournalEntryReversedAt,
                    preview.LifecycleMode,
                    preview.CanEditDraft,
                    preview.CanPostDraft,
                    preview.LifecycleReason,
                    LifecycleActions = preview.LifecycleActions.Select(action => new
                    {
                        action.ActionCode,
                        action.ActionLabel,
                        action.AvailabilityMode,
                        action.IsAvailable,
                        action.Reason
                    })
                });
            });

        accounting.MapGet(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/actions/{actionCode}",
            async (
                string sourceType,
                Guid documentId,
                string actionCode,
                [AsParameters] DocumentReviewLookupQuery query,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var preview = await repository.GetLifecycleActionPreviewAsync(
                        query.CompanyId,
                        sourceType,
                        documentId,
                        actionCode,
                        cancellationToken);

                    if (preview is null)
                    {
                        return Results.NotFound(new
                        {
                            message = "Source document lifecycle action preview was not found in the active company context."
                        });
                    }

                    return Results.Ok(new
                    {
                        preview.SourceType,
                        SourceTypeLabel = MapDocumentReviewSourceLabel(preview.SourceType),
                        preview.Id,
                        CompanyId = preview.CompanyId,
                        preview.EntityNumber,
                        preview.DisplayNumber,
                        preview.Status,
                        preview.JournalEntryId,
                        preview.JournalEntryDisplayNumber,
                        preview.JournalEntryStatus,
                        preview.JournalEntryPostedAt,
                        preview.JournalEntryVoidedAt,
                        preview.JournalEntryReversedAt,
                        preview.LifecycleMode,
                        preview.CanEditDraft,
                        preview.CanPostDraft,
                        preview.LifecycleReason,
                        Action = new
                        {
                            preview.ActionCode,
                            preview.ActionLabel,
                            preview.AvailabilityMode,
                            preview.IsAvailable,
                            preview.Reason
                        }
                    });
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new
                    {
                        message = ex.Message
                    });
                }
            });

        accounting.MapPost(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/void",
            async (
                string sourceType,
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var attempt = await repository.AttemptVoidAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    cancellationToken);

                if (attempt is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Source document void attempt could not find the document in the active company context."
                    });
                }

                var payload = new
                {
                    attempt.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(attempt.SourceType),
                    attempt.Id,
                    CompanyId = attempt.CompanyId,
                    attempt.EntityNumber,
                    attempt.DisplayNumber,
                    attempt.Status,
                    attempt.JournalEntryId,
                    attempt.JournalEntryDisplayNumber,
                    attempt.JournalEntryStatus,
                    attempt.LifecycleMode,
                    attempt.ActionCode,
                    attempt.ActionLabel,
                    attempt.AvailabilityMode,
                    attempt.ExecutionMode,
                    attempt.CommandAccepted,
                    attempt.Executed,
                    attempt.OutcomeCode,
                    Message = attempt.Message
                };

                return attempt.OutcomeCode switch
                {
                    "blocked" => Results.Conflict(payload),
                    "not_implemented" => Results.Json(payload, statusCode: StatusCodes.Status501NotImplemented),
                    "ready_for_implementation" => Results.Json(payload, statusCode: StatusCodes.Status501NotImplemented),
                    _ => Results.Ok(payload)
                };
            });

        accounting.MapPost(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse",
            async (
                string sourceType,
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var actorId = sessionAccessor.Current?.UserId;
                var attempt = await repository.AttemptReverseAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    actorId,
                    cancellationToken);

                if (attempt is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Source document reverse attempt could not find the document in the active company context."
                    });
                }

                var payload = new
                {
                    attempt.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(attempt.SourceType),
                    attempt.Id,
                    CompanyId = attempt.CompanyId,
                    attempt.EntityNumber,
                    attempt.DisplayNumber,
                    attempt.Status,
                    attempt.JournalEntryId,
                    attempt.JournalEntryDisplayNumber,
                    attempt.JournalEntryStatus,
                    attempt.LifecycleMode,
                    attempt.ActionCode,
                    attempt.ActionLabel,
                    attempt.AvailabilityMode,
                    attempt.ExecutionMode,
                    attempt.CommandAccepted,
                    attempt.Executed,
                    attempt.RequestId,
                    attempt.Persisted,
                    attempt.OutcomeCode,
                    Message = attempt.Message
                };

                return attempt.OutcomeCode switch
                {
                    "blocked" => Results.Conflict(payload),
                    "request_already_open" => Results.Conflict(payload),
                    "request_recorded" => Results.Json(payload, statusCode: StatusCodes.Status202Accepted),
                    _ => Results.Ok(payload)
                };
            });

        accounting.MapGet(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request",
            async (
                string sourceType,
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var request = await repository.GetLatestReverseRequestAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    cancellationToken);

                if (request is null)
                {
                    return Results.NotFound(new
                    {
                        message = "No reverse request has been recorded for this source document in the active company context."
                    });
                }

                return Results.Ok(new
                {
                    request.RequestId,
                    CompanyId = request.CompanyId,
                    request.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
                    Id = request.DocumentId,
                    request.EntityNumber,
                    request.DisplayNumber,
                    request.Status,
                    request.JournalEntryId,
                    request.JournalEntryDisplayNumber,
                    request.JournalEntryStatus,
                    request.LifecycleMode,
                    request.ActionCode,
                    request.ActionLabel,
                    request.AvailabilityMode,
                    request.IsAvailable,
                    request.Reason,
                    request.RequestStatus,
                    RequestedByActorType = request.RequestedByActorType,
                    RequestedByActorId = request.RequestedByActorId,
                    request.RequestedAt,
                    SubmittedByActorType = request.SubmittedByActorType,
                    SubmittedByActorId = request.SubmittedByActorId,
                    request.SubmittedAt,
                    CancelledByActorType = request.CancelledByActorType,
                    CancelledByActorId = request.CancelledByActorId,
                    request.CancelledAt,
                    request.ExecutionStatus,
                    ExecutionRequestedByActorType = request.ExecutionRequestedByActorType,
                    ExecutionRequestedByActorId = request.ExecutionRequestedByActorId,
                    request.ExecutionRequestedAt,
                    ExecutionCompletedByActorType = request.ExecutionCompletedByActorType,
                    ExecutionCompletedByActorId = request.ExecutionCompletedByActorId,
                    request.ExecutionCompletedAt,
                    request.CompensationJournalEntryId,
                    request.CompensationJournalEntryDisplayNumber,
                    request.CompensationSourceType
                });
            });

        accounting.MapGet(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-blockers",
            async (
                string sourceType,
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var blockers = await repository.ListSubledgerReverseBlockersAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    cancellationToken);

                return Results.Ok(blockers.Select(blocker => new
                {
                    blocker.SettlementApplicationId,
                    blocker.ApplicationType,
                    blocker.SettlementSourceType,
                    SettlementSourceTypeLabel = MapDocumentReviewSourceLabel(blocker.SettlementSourceType),
                    blocker.SettlementSourceId,
                    blocker.SettlementSourceDisplayNumber,
                    blocker.SettlementSourceDocumentDate,
                    blocker.TargetOpenItemType,
                    blocker.TargetOpenItemId,
                    blocker.TargetSourceType,
                    TargetSourceTypeLabel = MapDocumentReviewSourceLabel(blocker.TargetSourceType),
                    blocker.TargetSourceId,
                    blocker.TargetSourceDisplayNumber,
                    blocker.AppliedAmountTx,
                    blocker.AppliedAmountBase,
                    blocker.SettlementFxRate,
                    blocker.RealizedFxAmount,
                    blocker.AppliedAt,
                    blocker.ReverseRequestId,
                    blocker.ReverseRequestStatus,
                    blocker.ReverseExecutionStatus,
                    blocker.ReverseRequestedAt
                }));
            });

        accounting.MapGet(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/settlement-application-reversals",
            async (
                string sourceType,
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var reversals = await repository.ListSettlementApplicationReversalsAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    cancellationToken);

                return Results.Ok(reversals.Select(reversal => new
                {
                    reversal.ReversalEventId,
                    reversal.RequestId,
                    reversal.SettlementApplicationId,
                    reversal.ApplicationType,
                    reversal.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(reversal.SourceType),
                    reversal.SourceId,
                    reversal.TargetOpenItemType,
                    reversal.TargetOpenItemId,
                    reversal.AppliedAmountTx,
                    reversal.AppliedAmountBase,
                    reversal.SettlementFxRate,
                    reversal.RealizedFxAmount,
                    reversal.OriginalApplicationCreatedAt,
                    reversal.OriginalApplicationCreatedByUserId,
                    reversal.ReversedAt,
                    reversal.ReversedByActorType,
                    reversal.ReversedByActorId,
                    reversal.ReversalMode
                }));
            });

        accounting.MapPost(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/submit",
            async (
                string sourceType,
                Guid documentId,
                Guid requestId,
                [AsParameters] DocumentReviewLookupQuery query,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var actorId = sessionAccessor.Current?.UserId;
                var result = await repository.SubmitReverseRequestAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    requestId,
                    actorId,
                    cancellationToken);

                if (result is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Reverse request could not be found in the active company context."
                    });
                }

                var request = result.Request;
                var payload = new
                {
                    result.TransitionCode,
                    result.OutcomeCode,
                    Message = result.Message,
                    request.RequestId,
                    CompanyId = request.CompanyId,
                    request.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
                    Id = request.DocumentId,
                    request.EntityNumber,
                    request.DisplayNumber,
                    request.Status,
                    request.JournalEntryId,
                    request.JournalEntryDisplayNumber,
                    request.JournalEntryStatus,
                    request.LifecycleMode,
                    request.ActionCode,
                    request.ActionLabel,
                    request.AvailabilityMode,
                    request.IsAvailable,
                    request.Reason,
                    request.RequestStatus,
                    RequestedByActorType = request.RequestedByActorType,
                    RequestedByActorId = request.RequestedByActorId,
                    request.RequestedAt,
                    SubmittedByActorType = request.SubmittedByActorType,
                    SubmittedByActorId = request.SubmittedByActorId,
                    request.SubmittedAt,
                    CancelledByActorType = request.CancelledByActorType,
                    CancelledByActorId = request.CancelledByActorId,
                    request.CancelledAt,
                    request.ExecutionStatus,
                    ExecutionRequestedByActorType = request.ExecutionRequestedByActorType,
                    ExecutionRequestedByActorId = request.ExecutionRequestedByActorId,
                    request.ExecutionRequestedAt
                };

                return result.OutcomeCode switch
                {
                    "submitted" => Results.Ok(payload),
                    _ => Results.Conflict(payload)
                };
            });

        accounting.MapPost(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/cancel",
            async (
                string sourceType,
                Guid documentId,
                Guid requestId,
                [AsParameters] DocumentReviewLookupQuery query,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var actorId = sessionAccessor.Current?.UserId;
                var result = await repository.CancelReverseRequestAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    requestId,
                    actorId,
                    cancellationToken);

                if (result is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Reverse request could not be found in the active company context."
                    });
                }

                var request = result.Request;
                var payload = new
                {
                    result.TransitionCode,
                    result.OutcomeCode,
                    Message = result.Message,
                    request.RequestId,
                    CompanyId = request.CompanyId,
                    request.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
                    Id = request.DocumentId,
                    request.EntityNumber,
                    request.DisplayNumber,
                    request.Status,
                    request.JournalEntryId,
                    request.JournalEntryDisplayNumber,
                    request.JournalEntryStatus,
                    request.LifecycleMode,
                    request.ActionCode,
                    request.ActionLabel,
                    request.AvailabilityMode,
                    request.IsAvailable,
                    request.Reason,
                    request.RequestStatus,
                    RequestedByActorType = request.RequestedByActorType,
                    RequestedByActorId = request.RequestedByActorId,
                    request.RequestedAt,
                    SubmittedByActorType = request.SubmittedByActorType,
                    SubmittedByActorId = request.SubmittedByActorId,
                    request.SubmittedAt,
                    CancelledByActorType = request.CancelledByActorType,
                    CancelledByActorId = request.CancelledByActorId,
                    request.CancelledAt,
                    request.ExecutionStatus,
                    ExecutionRequestedByActorType = request.ExecutionRequestedByActorType,
                    ExecutionRequestedByActorId = request.ExecutionRequestedByActorId,
                    request.ExecutionRequestedAt
                };

                return result.OutcomeCode switch
                {
                    "cancelled" => Results.Ok(payload),
                    _ => Results.Conflict(payload)
                };
            });

        accounting.MapGet(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/apply-readiness",
            async (
                string sourceType,
                Guid documentId,
                Guid requestId,
                [AsParameters] DocumentLifecycleRequestReadinessQuery query,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var readiness = await repository.GetReverseRequestApplyReadinessAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    requestId,
                    asOfDate,
                    cancellationToken);

                if (readiness is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Reverse request could not be found in the active company context."
                    });
                }

                var request = readiness.Request;
                return Results.Ok(new
                {
                    request.RequestId,
                    CompanyId = request.CompanyId,
                    request.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
                    Id = request.DocumentId,
                    request.EntityNumber,
                    request.DisplayNumber,
                    request.Status,
                    request.RequestStatus,
                    request.LifecycleMode,
                    AsOfDate = readiness.AsOfDate,
                    readiness.GovernanceReady,
                    readiness.ApplyReady,
                    readiness.ExecutionMode,
                    readiness.AvailabilityMode,
                    readiness.IsAvailable,
                    readiness.Reason
                });
            });

        accounting.MapPost(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/execute",
            async (
                string sourceType,
                Guid documentId,
                Guid requestId,
                [AsParameters] DocumentLifecycleRequestReadinessQuery query,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountingDocumentReviewRepository repository,
                GlIJournalEntryLifecycleWorkflow journalEntryLifecycleWorkflow,
                IUnitOfWork unitOfWork,
                CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var actorId = sessionAccessor.Current?.UserId;

                // P0-1 (C1): wrap the audit-log mutation
                // (ExecuteReverseRequestAsync writes the "reverse_execution_requested"
                // transition) and the cross-table state changes
                // (CompleteReverseRequestExecutionAsync: settlement unapply +
                // open-item void + source-doc mark + task billing audit) in a single
                // IUnitOfWork.ExecuteAsync. Without this wrap, a failure inside
                // CompleteReverseRequestExecutionAsync — which opens a
                // PostgresCommandScope but no transaction in connection-only mode —
                // could half-commit the open-item void while leaving the source
                // document still flagged as posted. The linked-JE reverse step
                // (journalEntryLifecycleWorkflow.ReverseAsync) keeps its own short
                // transaction; on retry its own idempotency
                // (TryFindExistingCompensationAsync) short-circuits cleanly inside
                // the new outer UoW.
                var actorRequired = false;
                AccountingDocumentLifecycleRequestExecutionResult? result;
                try
                {
                    result = await unitOfWork.ExecuteAsync(async ct =>
                    {
                        var step1 = await repository.ExecuteReverseRequestAsync(
                            query.CompanyId,
                            sourceType,
                            documentId,
                            requestId,
                            actorId,
                            asOfDate,
                            ct);

                        if (step1 is null)
                        {
                            return null;
                        }

                        var step1Request = step1.Request;
                        var shouldRunLinkedJournalEntryReverse =
                            step1Request.JournalEntryId.HasValue &&
                            string.Equals(step1Request.ExecutionStatus, "execution_requested", StringComparison.Ordinal) &&
                            step1Request.ExecutionCompletedAt is null;

                        if (!shouldRunLinkedJournalEntryReverse)
                        {
                            return step1;
                        }

                        if (!actorId.HasValue)
                        {
                            // Preserve the original behaviour: step 1's audit row
                            // stays committed (status flips to "execution_requested"),
                            // but step 2/3 are skipped and the caller gets BadRequest
                            // so they can re-issue with a real business session.
                            actorRequired = true;
                            return step1;
                        }

                        var lifecycleResult = await journalEntryLifecycleWorkflow.ReverseAsync(
                            query.CompanyId,
                            step1Request.JournalEntryId!.Value,
                            actorId.Value,
                            ct);

                        return await repository.CompleteReverseRequestExecutionAsync(
                                query.CompanyId,
                                sourceType,
                                documentId,
                                requestId,
                                actorId,
                                lifecycleResult.CompensationJournalEntryId,
                                lifecycleResult.CompensationDisplayNumber,
                                lifecycleResult.CompensationSourceType,
                                lifecycleResult.LifecycleAt,
                                ct)
                            ?? step1;
                    }, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    // The linked-JE reverse rejected the operation (e.g.
                    // JournalEntryLifecycleException). The UoW has rolled back step 1
                    // (audit log row) and step 3 was not reached. Re-read the current
                    // persisted state so the conflict response reflects the actual
                    // post-rollback status, not the in-flight snapshot that just got
                    // undone.
                    var current = await repository.GetReverseRequestAsync(
                        query.CompanyId,
                        sourceType,
                        documentId,
                        requestId,
                        cancellationToken);

                    if (current is null)
                    {
                        return Results.NotFound(new
                        {
                            message = "Reverse request could not be found in the active company context."
                        });
                    }

                    return Results.Conflict(new
                    {
                        code = ResolveAccountingOperationErrorCode(ex.Message),
                        current.RequestId,
                        CompanyId = current.CompanyId,
                        current.SourceType,
                        SourceTypeLabel = MapDocumentReviewSourceLabel(current.SourceType),
                        Id = current.DocumentId,
                        current.EntityNumber,
                        current.DisplayNumber,
                        current.Status,
                        current.RequestStatus,
                        current.ExecutionStatus,
                        AsOfDate = asOfDate,
                        ExecutionMode = "governed_execution_orchestration",
                        Message = ex.Message
                    });
                }

                if (actorRequired)
                {
                    return Results.BadRequest(new
                    {
                        message = "A business-session user is required before governed reverse execution can reverse the linked journal entry."
                    });
                }

                if (result is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Reverse request could not be found in the active company context."
                    });
                }

                var request = result.Request;
                var payload = new
                {
                    request.RequestId,
                    CompanyId = request.CompanyId,
                    request.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
                    Id = request.DocumentId,
                    request.EntityNumber,
                    request.DisplayNumber,
                    request.Status,
                    request.RequestStatus,
                    request.ExecutionStatus,
                    AsOfDate = result.AsOfDate,
                    result.ExecutionMode,
                    result.CommandAccepted,
                    result.Executed,
                    result.Persisted,
                    result.OutcomeCode,
                    Message = result.Message,
                    ExecutionRequestedByActorType = request.ExecutionRequestedByActorType,
                    ExecutionRequestedByActorId = request.ExecutionRequestedByActorId,
                    request.ExecutionRequestedAt,
                    ExecutionCompletedByActorType = request.ExecutionCompletedByActorType,
                    ExecutionCompletedByActorId = request.ExecutionCompletedByActorId,
                    request.ExecutionCompletedAt,
                    request.CompensationJournalEntryId,
                    request.CompensationJournalEntryDisplayNumber,
                    request.CompensationSourceType
                };

                return result.OutcomeCode switch
                {
                    "blocked" or "blocked_by_subledger_truth" or "blocked_by_missing_linked_journal_entry" => Results.BadRequest(payload),
                    "execution_already_requested" or "execution_already_completed" => Results.Conflict(payload),
                    "execution_request_recorded" => Results.Json(payload, statusCode: StatusCodes.Status202Accepted),
                    _ => Results.Ok(payload)
                };
            });

        accounting.MapGet(
            "/source-document-lifecycle/{sourceType}/{documentId:guid}/reverse-request/{requestId:guid}/execution-plan",
            async (
                string sourceType,
                Guid documentId,
                Guid requestId,
                [AsParameters] DocumentLifecycleRequestReadinessQuery query,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var plan = await repository.GetReverseRequestExecutionPlanAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    requestId,
                    asOfDate,
                    cancellationToken);

                if (plan is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Reverse request could not be found in the active company context."
                    });
                }

                var request = plan.Request;
                return Results.Ok(new
                {
                    request.RequestId,
                    CompanyId = request.CompanyId,
                    request.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
                    Id = request.DocumentId,
                    request.EntityNumber,
                    request.DisplayNumber,
                    request.Status,
                    request.RequestStatus,
                    request.ExecutionStatus,
                    request.LifecycleMode,
                    AsOfDate = plan.AsOfDate,
                    plan.ExecutionMode,
                    plan.CanExecute,
                    plan.OverallStatus,
                    plan.Reason,
                    Steps = plan.Steps.Select(step => new
                    {
                        step.StepNumber,
                        step.StepCode,
                        step.StepLabel,
                        step.StepStatus,
                        step.Reason
                    })
                });
            });

        accounting.MapGet(
            "/document-review/{sourceType}/{documentId:guid}",
            async (
                string sourceType,
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                IAccountingDocumentReviewRepository repository,
                CancellationToken cancellationToken) =>
            {
                var review = await repository.GetSourceDocumentAsync(
                    query.CompanyId,
                    sourceType,
                    documentId,
                    cancellationToken);

                if (review is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Source document review was not found in the active company context."
                    });
                }

                return Results.Ok(new
                {
                    review.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(review.SourceType),
                    review.Id,
                    CompanyId = review.CompanyId,
                    review.EntityNumber,
                    review.DisplayNumber,
                    review.Status,
                    review.DocumentDate,
                    review.DueDate,
                    CounterpartyLabel = MapDocumentReviewCounterpartyLabel(review.CounterpartyRole),
                    review.CounterpartyId,
                    ControlAccountLabel = MapDocumentReviewControlAccountLabel(review.CounterpartyRole),
                    review.ControlAccountId,
                    review.JournalEntryId,
                    review.JournalEntryDisplayNumber,
                    review.JournalEntryStatus,
                    review.JournalEntryPostedAt,
                    review.JournalEntryVoidedAt,
                    review.JournalEntryReversedAt,
                    review.LifecycleMode,
                    review.CanEditDraft,
                    review.CanPostDraft,
                    review.LifecycleReason,
                    LifecycleActions = review.LifecycleActions.Select(action => new
                    {
                        action.ActionCode,
                        action.ActionLabel,
                        action.AvailabilityMode,
                        action.IsAvailable,
                        action.Reason
                    }),
                    review.TransactionCurrencyCode,
                    review.BaseCurrencyCode,
                    review.SubtotalAmount,
                    review.TaxAmount,
                    review.TotalAmount,
                    review.Memo,
                    Lines = review.Lines.Select(line => new
                    {
                        line.LineNumber,
                        line.AccountId,
                        line.AccountCode,
                        line.AccountName,
                        AccountLabel = MapDocumentReviewLineAccountLabel(review.CounterpartyRole),
                        line.Description,
                        line.Quantity,
                        line.UnitPrice,
                        line.LineAmount,
                        line.TaxAmount,
                        line.IsTaxRecoverable,
                        line.TaxAccountId,
                        line.TxDebit,
                        line.TxCredit,
                        line.SourceOpenItemId,
                        line.SourceDocumentType,
                        line.SourceDocumentId,
                        line.SourceDocumentDisplayNumber,
                        line.TargetOpenItemId,
                        line.TargetDocumentType,
                        line.TargetDocumentId,
                        line.TargetDocumentDisplayNumber
                    })
                });
            });

        // ---------------------------------------------------------------------------
        // Invoice PDF download (Batch 1 of the invoice send / template work).
        //
        // Returns the invoice as a PDF byte stream rendered through QuestPDF using a
        // fixed "default" template. The HTML invoice preview that lands in Batch 4
        // will share the same InvoiceRenderModel + builder so the bytes the user
        // downloads always match the on-screen preview. Subsequent batches will
        // thread an InvoiceTemplate snapshot through here for branding overrides.
        // ---------------------------------------------------------------------------
        accounting.MapGet(
            "/document-review/invoice/{documentId:guid}/pdf",
            async (
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                Guid? templateId,
                IAccountingDocumentReviewRepository reviewRepository,
                ICustomerStore customerStore,
                ICompanyProfileQuery companyProfileQuery,
                IInvoiceTemplateStore templateStore,
                IInvoicePdfRenderer renderer,
                CancellationToken cancellationToken) =>
            {
                var companyId = CompanyId.Parse(query.CompanyId.ToString());

                var review = await reviewRepository.GetSourceDocumentAsync(
                    companyId,
                    "invoice",
                    documentId,
                    cancellationToken);

                if (review is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Invoice was not found in the active company context."
                    });
                }

                var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
                if (company is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Company profile is not provisioned. Run the SysAdmin First-Company Wizard before downloading invoice PDFs."
                    });
                }

                CustomerRecord? customer = null;
                if (review.CounterpartyId is { } counterpartyId)
                {
                    customer = await customerStore.GetByIdAsync(query.CompanyId, counterpartyId, cancellationToken);
                }

                // Optional ?templateId override lets the template editor's
                // "Download sample" button preview an unsaved draft against a
                // real invoice. Default path uses the company's default template.
                InvoiceTemplate? template = null;
                if (templateId is { } overrideId)
                {
                    template = await templateStore.GetByIdAsync(query.CompanyId, overrideId, cancellationToken);
                }
                template ??= await templateStore.GetDefaultAsync(query.CompanyId, cancellationToken);

                var projection = new InvoiceReviewProjection(
                    DisplayNumber: review.DisplayNumber,
                    EntityNumber: review.EntityNumber,
                    DocumentDate: review.DocumentDate,
                    DueDate: review.DueDate,
                    Status: review.Status,
                    CounterpartyDisplayName: customer?.DisplayName,
                    TransactionCurrencyCode: review.TransactionCurrencyCode,
                    SubtotalAmount: review.SubtotalAmount,
                    TaxAmount: review.TaxAmount,
                    TotalAmount: review.TotalAmount,
                    Memo: review.Memo,
                    Lines: review.Lines.Select(line => new InvoiceReviewLineProjection(
                        LineNumber: line.LineNumber,
                        Description: line.Description,
                        Quantity: line.Quantity,
                        UnitPrice: line.UnitPrice,
                        LineAmount: line.LineAmount,
                        TaxAmount: line.TaxAmount)).ToArray());

                var renderModel = InvoiceRenderModelBuilder.Build(projection, company, customer, template?.Config);
                var pdfBytes = renderer.Render(renderModel);

                return Results.File(
                    fileContents: pdfBytes,
                    contentType: "application/pdf",
                    fileDownloadName: $"{review.DisplayNumber}.pdf");
            });

        // ---------------------------------------------------------------------------
        // Send invoice by email (Batch 2). Composes subject + HTML body + plain-text
        // body from the InvoiceRenderModel + an optional operator-typed note,
        // renders the same PDF the Download-PDF endpoint serves, ships through the
        // platform's SMTP options, and writes one row to invoice_send_history
        // regardless of outcome (so audit always captures both successful and
        // failed sends).
        // ---------------------------------------------------------------------------
        accounting.MapPost(
            "/document-review/invoice/{documentId:guid}/send",
            async (
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                InvoiceSendHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountingDocumentReviewRepository reviewRepository,
                ICustomerStore customerStore,
                ICompanyProfileQuery companyProfileQuery,
                IInvoiceTemplateStore templateStore,
                IInvoicePdfRenderer renderer,
                IInvoiceEmailSender emailSender,
                IInvoiceSendHistoryStore historyStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
                {
                    return Results.Unauthorized();
                }

                if (string.IsNullOrWhiteSpace(request.ToEmail) ||
                    !request.ToEmail.Contains('@', StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { message = "A recipient email is required." });
                }

                var companyId = CompanyId.Parse(query.CompanyId.ToString());

                var review = await reviewRepository.GetSourceDocumentAsync(
                    companyId,
                    "invoice",
                    documentId,
                    cancellationToken);
                if (review is null)
                {
                    return Results.NotFound(new { message = "Invoice not found in the active company context." });
                }

                var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
                if (company is null)
                {
                    return Results.NotFound(new { message = "Company profile is not provisioned." });
                }

                CustomerRecord? customer = null;
                if (review.CounterpartyId is { } counterpartyId)
                {
                    customer = await customerStore.GetByIdAsync(query.CompanyId, counterpartyId, cancellationToken);
                }

                var template = await templateStore.GetDefaultAsync(query.CompanyId, cancellationToken);

                var projection = new InvoiceReviewProjection(
                    DisplayNumber: review.DisplayNumber,
                    EntityNumber: review.EntityNumber,
                    DocumentDate: review.DocumentDate,
                    DueDate: review.DueDate,
                    Status: review.Status,
                    CounterpartyDisplayName: customer?.DisplayName,
                    TransactionCurrencyCode: review.TransactionCurrencyCode,
                    SubtotalAmount: review.SubtotalAmount,
                    TaxAmount: review.TaxAmount,
                    TotalAmount: review.TotalAmount,
                    Memo: review.Memo,
                    Lines: review.Lines.Select(line => new InvoiceReviewLineProjection(
                        LineNumber: line.LineNumber,
                        Description: line.Description,
                        Quantity: line.Quantity,
                        UnitPrice: line.UnitPrice,
                        LineAmount: line.LineAmount,
                        TaxAmount: line.TaxAmount)).ToArray());

                var renderModel = InvoiceRenderModelBuilder.Build(projection, company, customer, template?.Config);
                var pdfBytes = renderer.Render(renderModel);
                var composition = InvoiceEmailComposer.Compose(
                    renderModel,
                    request.Message,
                    subjectTemplate: template?.Config.EmailSubjectTemplate);

                var ccList = SplitEmailList(request.Cc);
                var bccList = SplitEmailList(request.Bcc);

                var emailRequest = new InvoiceEmailRequest(
                    ToEmail: request.ToEmail.Trim(),
                    ToDisplayName: customer?.DisplayName ?? string.Empty,
                    CcEmails: ccList,
                    BccEmails: bccList,
                    Subject: composition.Subject,
                    HtmlBody: composition.HtmlBody,
                    PlainTextBody: composition.PlainTextBody,
                    AttachmentFileName: $"{review.DisplayNumber}.pdf",
                    AttachmentBytes: pdfBytes);

                var sendResult = await emailSender.SendAsync(emailRequest, cancellationToken);

                var historyRecord = await historyStore.RecordAsync(
                    new InvoiceSendHistoryDraft(
                        CompanyId: query.CompanyId,
                        InvoiceId: documentId,
                        SentByUserId: session.UserId,
                        ToEmail: emailRequest.ToEmail,
                        CcEmails: string.Join(", ", ccList),
                        BccEmails: string.Join(", ", bccList),
                        Subject: composition.Subject,
                        Status: sendResult.Succeeded ? "sent" : "failed",
                        ErrorMessage: sendResult.ErrorMessage),
                    cancellationToken);

                if (!sendResult.Succeeded)
                {
                    return Results.UnprocessableEntity(new
                    {
                        succeeded = false,
                        message = sendResult.ErrorMessage ?? "Email delivery failed.",
                        historyId = historyRecord.Id,
                        sentAt = historyRecord.SentAt,
                    });
                }

                return Results.Ok(new
                {
                    succeeded = true,
                    historyId = historyRecord.Id,
                    sentAt = historyRecord.SentAt,
                    toEmail = historyRecord.ToEmail,
                    subject = historyRecord.Subject,
                });
            })
            .RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArInvoiceSend)
            .RequireRateLimiting("invoice-send");

        // ---------------------------------------------------------------------------
        // Read-only view of the invoice's send history. Powers the "Last sent"
        // badge on the document detail page and (later) the timeline panel that
        // exposes failed attempts for re-send.
        // ---------------------------------------------------------------------------
        accounting.MapGet(
            "/document-review/invoice/{documentId:guid}/send-history",
            async (
                Guid documentId,
                [AsParameters] DocumentReviewLookupQuery query,
                IInvoiceSendHistoryStore historyStore,
                CancellationToken cancellationToken) =>
            {
                var rows = await historyStore.ListByInvoiceAsync(
                    query.CompanyId,
                    documentId,
                    limit: 50,
                    cancellationToken);

                return Results.Ok(rows.Select(r => new
                {
                    r.Id,
                    r.SentAt,
                    r.SentByUserId,
                    r.ToEmail,
                    r.CcEmails,
                    r.BccEmails,
                    r.Subject,
                    r.Status,
                    r.ErrorMessage,
                }).ToArray());
            });

        // ---------------------------------------------------------------------------
        // Invoice templates (Batch 3). One per-company table with three lazy-
        // seeded starters (Modern / Classic / Minimal). The default flows
        // through every PDF / email send. Endpoints:
        //   GET  /invoice-templates                       -> list
        //   GET  /invoice-templates/{id}                  -> single
        //   POST /invoice-templates                       -> create (empty -> default config copy)
        //   PUT  /invoice-templates/{id}                  -> update name + config
        //   POST /invoice-templates/{id}/set-default      -> mark as default (single transaction)
        // ---------------------------------------------------------------------------
        accounting.MapGet(
            "/invoice-templates",
            async (
                [AsParameters] DocumentReviewLookupQuery query,
                IInvoiceTemplateStore store,
                CancellationToken cancellationToken) =>
            {
                var templates = await store.ListByCompanyAsync(query.CompanyId, cancellationToken);
                return Results.Ok(templates.Select(MapInvoiceTemplate).ToArray());
            });

        accounting.MapGet(
            "/invoice-templates/{templateId:guid}",
            async (
                Guid templateId,
                [AsParameters] DocumentReviewLookupQuery query,
                IInvoiceTemplateStore store,
                CancellationToken cancellationToken) =>
            {
                var template = await store.GetByIdAsync(query.CompanyId, templateId, cancellationToken);
                return template is null
                    ? Results.NotFound(new { message = "Invoice template not found in this company." })
                    : Results.Ok(MapInvoiceTemplate(template));
            });

        accounting.MapPost(
            "/invoice-templates",
            async (
                [AsParameters] DocumentReviewLookupQuery query,
                InvoiceTemplateUpsertHttpRequest request,
                IInvoiceTemplateStore store,
                CancellationToken cancellationToken) =>
            {
                var (config, validationError) = TryReadInvoiceTemplateConfig(request);
                if (validationError is not null)
                {
                    return Results.BadRequest(new { message = validationError });
                }

                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest(new { message = "Template name is required." });
                }

                var created = await store.CreateAsync(
                    query.CompanyId,
                    new InvoiceTemplateUpsertRequest(request.Name.Trim(), config),
                    cancellationToken);

                return Results.Created(
                    $"/accounting/invoice-templates/{created.Id:D}?companyId={query.CompanyId:D}",
                    MapInvoiceTemplate(created));
            });

        accounting.MapPut(
            "/invoice-templates/{templateId:guid}",
            async (
                Guid templateId,
                [AsParameters] DocumentReviewLookupQuery query,
                InvoiceTemplateUpsertHttpRequest request,
                IInvoiceTemplateStore store,
                CancellationToken cancellationToken) =>
            {
                var (config, validationError) = TryReadInvoiceTemplateConfig(request);
                if (validationError is not null)
                {
                    return Results.BadRequest(new { message = validationError });
                }

                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest(new { message = "Template name is required." });
                }

                var updated = await store.UpdateAsync(
                    query.CompanyId,
                    templateId,
                    new InvoiceTemplateUpsertRequest(request.Name.Trim(), config),
                    cancellationToken);

                return updated is null
                    ? Results.NotFound(new { message = "Invoice template not found in this company." })
                    : Results.Ok(MapInvoiceTemplate(updated));
            });

        accounting.MapPost(
            "/invoice-templates/{templateId:guid}/set-default",
            async (
                Guid templateId,
                [AsParameters] DocumentReviewLookupQuery query,
                IInvoiceTemplateStore store,
                CancellationToken cancellationToken) =>
            {
                var defaulted = await store.SetDefaultAsync(query.CompanyId, templateId, cancellationToken);
                return defaulted is null
                    ? Results.NotFound(new { message = "Invoice template not found in this company." })
                    : Results.Ok(MapInvoiceTemplate(defaulted));
            });

        // ---------------------------------------------------------------------------
        // Renders a PDF preview of the *draft* template (the unsaved upsert body),
        // so the Settings editor can show a byte-accurate "what your customer
        // sees" iframe that updates as the operator types. Uses the issuing
        // company's profile as the issuer block and a hard-coded sample invoice
        // (Acme Co. / two demo lines) as the bill-to + lines so the preview
        // works even before any real invoice exists.
        // ---------------------------------------------------------------------------
        accounting.MapPost(
            "/invoice-templates/preview-pdf",
            async (
                [AsParameters] DocumentReviewLookupQuery query,
                InvoiceTemplateUpsertHttpRequest request,
                ICompanyProfileQuery companyProfileQuery,
                IInvoicePdfRenderer renderer,
                CancellationToken cancellationToken) =>
            {
                var (config, validationError) = TryReadInvoiceTemplateConfig(request);
                if (validationError is not null)
                {
                    return Results.BadRequest(new { message = validationError });
                }

                var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
                if (company is null)
                {
                    return Results.NotFound(new { message = "Company profile is not provisioned." });
                }

                var sample = BuildSampleInvoiceProjection(company.BaseCurrencyCode);
                var renderModel = InvoiceRenderModelBuilder.Build(sample, company, customer: null, config);
                var pdfBytes = renderer.Render(renderModel);

                return Results.File(pdfBytes, "application/pdf");
            });
    }
}
