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
/// ReceiptGrIrAndVendorCredit endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class ReceiptGrIrAndVendorCreditEndpoints
{
    public static void MapReceiptGrIrAndVendorCreditEndpoints(this RouteGroupBuilder accounting)
    {

        accounting.MapGet(
            "/receipts",
            async (
                [AsParameters] ReceiptListQuery query,
                IReceiptDocumentRepository repository,
                IReceiptInventoryActivationStore activationStore,
                IReceiptInventoryValuationStore valuationStore,
                IReceiptInventoryCostLayerEmissionStore emissionStore,
                IReceiptGrIrBridgeStore grIrBridgeStore,
                IReceiptGrIrApSettlementControlStore grIrSettlementStore,
                CancellationToken cancellationToken) =>
            {
                var documents = await repository.ListAsync(
                    query.CompanyId,
                    query.Take ?? 50,
                    cancellationToken);
                var activationSummaries = await activationStore.GetReceiptActivationSummariesAsync(
                    query.CompanyId,
                    documents.Select(static document => document.DocumentId).ToArray(),
                    cancellationToken);
                var valuationSummaries = await valuationStore.GetReceiptValuationSummariesAsync(
                    query.CompanyId,
                    documents.Select(static document => document.DocumentId).ToArray(),
                    cancellationToken);
                var emissionSummaries = await emissionStore.GetReceiptCostLayerEmissionSummariesAsync(
                    query.CompanyId,
                    documents.Select(static document => document.DocumentId).ToArray(),
                    cancellationToken);
                var emissionReconciliationSummaries = await emissionStore.GetReceiptCostLayerEmissionReconciliationSummariesAsync(
                    query.CompanyId,
                    documents.Select(static document => document.DocumentId).ToArray(),
                    cancellationToken);
                var grIrBridgeSummaries = await grIrBridgeStore.GetReceiptGrIrBridgeSummariesAsync(
                    query.CompanyId,
                    documents.Select(static document => document.DocumentId).ToArray(),
                    cancellationToken);
                var grIrSettlementSummaries = await grIrSettlementStore.GetReceiptSettlementSummariesAsync(
                    query.CompanyId,
                    documents.Select(static document => document.DocumentId).ToArray(),
                    cancellationToken);

                return Results.Ok(documents.Select(document => new
                {
                    document.DocumentId,
                    document.EntityNumber,
                    document.DisplayNumber,
                    document.Status,
                    document.VendorId,
                    document.WarehouseId,
                    document.ReceiptDate,
                    document.LineCount,
                    document.TotalQuantity,
                    document.VendorReference,
                    document.SourceReference,
                    document.Memo,
                    document.CreatedAt,
                    document.UpdatedAt,
                    document.PostedAt,
                    InventoryActivation = activationSummaries.TryGetValue(document.DocumentId, out var summary)
                        ? new
                        {
                            summary.ReceiptStatus,
                            summary.ActivationStatus,
                            summary.InventoryDocumentId,
                            summary.ReceiptLineCount,
                            summary.ActivatedLineCount,
                            summary.TotalQuantity,
                            summary.ActivatedQuantity,
                            summary.ActivatedAt,
                            summary.LastFailureMessage,
                            summary.LastFailureAt
                        }
                        : null,
                    InventoryValuation = valuationSummaries.TryGetValue(document.DocumentId, out var valuationSummary)
                        ? new
                        {
                            valuationSummary.ValuationStatus,
                            valuationSummary.ActivatedQuantity,
                            valuationSummary.BillCoveredQuantity,
                            valuationSummary.ValuedQuantity,
                            valuationSummary.UnvaluedQuantity,
                            valuationSummary.ValuationLineCount,
                            valuationSummary.ValuationAmountBase,
                            valuationSummary.LastValuedAt
                        }
                        : null,
                    InventoryCostLayerEmission = emissionSummaries.TryGetValue(document.DocumentId, out var emissionSummary)
                        ? new
                        {
                            emissionSummary.EmissionStatus,
                            emissionSummary.ActivatedQuantity,
                            emissionSummary.ValuationBackedQuantity,
                            emissionSummary.EmissionEligibleQuantity,
                            emissionSummary.EmittedQuantity,
                            emissionSummary.UnemittedQuantity,
                            emissionSummary.EmissionLineCount,
                            emissionSummary.EmittedCostBase,
                            emissionSummary.LastEmittedAt
                        }
                        : null,
                    InventoryCostLayerEmissionReconciliation = emissionReconciliationSummaries.TryGetValue(document.DocumentId, out var reconciliationSummary)
                        ? new
                        {
                            reconciliationSummary.ReconciliationStatus,
                            reconciliationSummary.EmissionLineCount,
                            reconciliationSummary.CostLayerCount,
                            reconciliationSummary.MissingCostLayerCount,
                            reconciliationSummary.OrphanCostLayerCount,
                            reconciliationSummary.EmittedQuantity,
                            reconciliationSummary.CostLayerQuantity,
                            reconciliationSummary.EmittedCostBase,
                            reconciliationSummary.CostLayerOriginalCostBase,
                            reconciliationSummary.LastEmittedAt
                        }
                        : null,
                    GrIrBridge = grIrBridgeSummaries.TryGetValue(document.DocumentId, out var grIrBridgeSummary)
                        ? new
                        {
                            grIrBridgeSummary.BridgeStatus,
                            grIrBridgeSummary.BridgeLineCount,
                            grIrBridgeSummary.EligibleLineCount,
                            grIrBridgeSummary.BlockedReconciliationLineCount,
                            grIrBridgeSummary.BlockedVarianceLineCount,
                            grIrBridgeSummary.PostedLineCount,
                            grIrBridgeSummary.BridgeQuantity,
                            grIrBridgeSummary.BridgeAmountBase,
                            grIrBridgeSummary.EligibleAmountBase,
                            grIrBridgeSummary.BlockedAmountBase,
                            grIrBridgeSummary.PostedAmountBase,
                            grIrBridgeSummary.JournalEntryId,
                            grIrBridgeSummary.JournalEntryDisplayNumber,
                            grIrBridgeSummary.LastPostedAt,
                            grIrBridgeSummary.LastRefreshedAt
                        }
                        : null,
                    GrIrSettlement = grIrSettlementSummaries.TryGetValue(document.DocumentId, out var grIrSettlementSummary)
                        ? new
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
                        }
                        : null
                }));
            });

        accounting.MapGet(
            "/receipts/grir-clearing-account-policy",
            async (
                [AsParameters] ReceiptLookupQuery query,
                IReceiptGrIrClearingAccountPolicyRepository repository,
                CancellationToken cancellationToken) =>
            {
                var accountId = await repository.GetDefaultGrIrClearingAccountIdAsync(
                    query.CompanyId,
                    cancellationToken);

                return Results.Ok(new
                {
                    query.CompanyId,
                    GrIrClearingAccountId = accountId
                });
            });

        accounting.MapPost(
            "/receipts/grir-clearing-account-policy",
            async (
                SaveReceiptGrIrClearingAccountPolicyHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IReceiptGrIrClearingAccountPolicyRepository repository,
                CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequireGrIrClearingAccountPolicyManagementAuthority(
                    sessionAccessor.Current,
                    "save");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    await repository.SaveDefaultGrIrClearingAccountAsync(
                        request.CompanyId,
                        request.UserId,
                        request.GrIrClearingAccountId,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        request.CompanyId,
                        request.GrIrClearingAccountId
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapGet(
            "/receipts/{documentId:guid}",
            async (
                Guid documentId,
                [AsParameters] ReceiptLookupQuery query,
                IReceiptDocumentRepository repository,
                IReceiptInventoryActivationStore activationStore,
                IReceiptInventoryValuationStore valuationStore,
                IReceiptInventoryCostLayerEmissionStore emissionStore,
                IReceiptGrIrBridgeStore grIrBridgeStore,
                IReceiptGrIrApSettlementControlStore grIrSettlementStore,
                CancellationToken cancellationToken) =>
            {
                var document = await repository.GetAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);
                var activationSummary = await activationStore.GetReceiptActivationSummaryAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);
                var valuationSummary = await valuationStore.GetReceiptValuationSummaryAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);
                var emissionSummary = await emissionStore.GetReceiptCostLayerEmissionSummaryAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);
                var emissionReconciliationSummary = await emissionStore.GetReceiptCostLayerEmissionReconciliationSummaryAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);
                var grIrBridgeSummary = await grIrBridgeStore.GetReceiptGrIrBridgeSummaryAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);
                var grIrSettlementSummary = await grIrSettlementStore.GetReceiptSettlementSummaryAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                return document is null
                    ? Results.NotFound(new { message = "Receipt document was not found in the active company context." })
                    : Results.Ok(new
                    {
                        document.Id,
                        CompanyId = document.CompanyId,
                        EntityNumber = document.EntityNumber.Value,
                        DisplayNumber = document.DisplayNumber.Value,
                        document.SourceType,
                        document.Status,
                        document.VendorId,
                        document.WarehouseId,
                        document.ReceiptDate,
                        document.VendorReference,
                        document.SourceReference,
                        document.Memo,
                        document.PostedAt,
                        InventoryActivation = activationSummary is null
                            ? null
                            : new
                            {
                                activationSummary.ReceiptStatus,
                                activationSummary.ActivationStatus,
                                activationSummary.InventoryDocumentId,
                                activationSummary.ReceiptLineCount,
                                activationSummary.ActivatedLineCount,
                                activationSummary.TotalQuantity,
                                activationSummary.ActivatedQuantity,
                                activationSummary.ActivatedAt,
                                activationSummary.LastFailureMessage,
                                activationSummary.LastFailureAt
                            },
                        InventoryValuation = valuationSummary is null
                            ? null
                            : new
                            {
                                valuationSummary.ValuationStatus,
                                valuationSummary.ActivatedQuantity,
                                valuationSummary.BillCoveredQuantity,
                                valuationSummary.ValuedQuantity,
                                valuationSummary.UnvaluedQuantity,
                                valuationSummary.ValuationLineCount,
                                valuationSummary.ValuationAmountBase,
                                valuationSummary.LastValuedAt
                            },
                        InventoryCostLayerEmission = emissionSummary is null
                            ? null
                            : new
                            {
                                emissionSummary.EmissionStatus,
                                emissionSummary.ActivatedQuantity,
                                emissionSummary.ValuationBackedQuantity,
                                emissionSummary.EmissionEligibleQuantity,
                                emissionSummary.EmittedQuantity,
                                emissionSummary.UnemittedQuantity,
                                emissionSummary.EmissionLineCount,
                                emissionSummary.EmittedCostBase,
                                emissionSummary.LastEmittedAt
                            },
                        InventoryCostLayerEmissionReconciliation = emissionReconciliationSummary is null
                            ? null
                            : new
                            {
                                emissionReconciliationSummary.ReconciliationStatus,
                                emissionReconciliationSummary.EmissionLineCount,
                                emissionReconciliationSummary.CostLayerCount,
                                emissionReconciliationSummary.MissingCostLayerCount,
                                emissionReconciliationSummary.OrphanCostLayerCount,
                                emissionReconciliationSummary.EmittedQuantity,
                                emissionReconciliationSummary.CostLayerQuantity,
                                emissionReconciliationSummary.EmittedCostBase,
                                emissionReconciliationSummary.CostLayerOriginalCostBase,
                                emissionReconciliationSummary.LastEmittedAt
                            },
                        GrIrBridge = grIrBridgeSummary is null
                            ? null
                            : new
                            {
                                grIrBridgeSummary.BridgeStatus,
                                grIrBridgeSummary.BridgeLineCount,
                                grIrBridgeSummary.EligibleLineCount,
                                grIrBridgeSummary.BlockedReconciliationLineCount,
                                grIrBridgeSummary.BlockedVarianceLineCount,
                                grIrBridgeSummary.PostedLineCount,
                                grIrBridgeSummary.BridgeQuantity,
                                grIrBridgeSummary.BridgeAmountBase,
                                grIrBridgeSummary.EligibleAmountBase,
                                grIrBridgeSummary.BlockedAmountBase,
                                grIrBridgeSummary.PostedAmountBase,
                                grIrBridgeSummary.JournalEntryId,
                                grIrBridgeSummary.JournalEntryDisplayNumber,
                                grIrBridgeSummary.LastPostedAt,
                                grIrBridgeSummary.LastRefreshedAt
                            },
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
                        Lines = document.ReceiptLines.Select(line => new
                        {
                            line.LineNumber,
                            line.ItemId,
                            line.Quantity,
                            line.UomCode,
                            line.TrackingCaptureHome,
                            line.PurchaseOrderId,
                            line.PurchaseOrderLineNumber
                        })
                    });
            });

        accounting.MapPost(
            "/receipts/drafts",
            async (SaveReceiptDraftHttpRequest request, IReceiptDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.SaveDraftAsync(
                        new ReceiptDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.WarehouseId,
                            request.ReceiptDate,
                            request.VendorReference,
                            request.SourceReference,
                            request.Memo,
                            request.Lines.Select(static line => new ReceiptDraftLineSaveModel(
                                line.LineNumber,
                                line.ItemId,
                                line.Quantity,
                                line.UomCode,
                                line.TrackingCaptureHome,
                                line.PurchaseOrderId,
                                line.PurchaseOrderLineNumber)).ToArray()),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPut(
            "/receipts/drafts/{documentId:guid}",
            async (Guid documentId, SaveReceiptDraftHttpRequest request, IReceiptDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.SaveDraftAsync(
                        new ReceiptDraftSaveModel(
                            documentId,
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.WarehouseId,
                            request.ReceiptDate,
                            request.VendorReference,
                            request.SourceReference,
                            request.Memo,
                            request.Lines.Select(static line => new ReceiptDraftLineSaveModel(
                                line.LineNumber,
                                line.ItemId,
                                line.Quantity,
                                line.UomCode,
                                line.TrackingCaptureHome,
                                line.PurchaseOrderId,
                                line.PurchaseOrderLineNumber)).ToArray()),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/receipts/{documentId:guid}/post",
            async (Guid documentId, PostReceiptDraftHttpRequest request, PostReceiptWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await workflow.PostAsync(
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);

        accounting.MapPost(
            "/receipts/{documentId:guid}/inventory-activation/retry",
            async (Guid documentId, PostReceiptDraftHttpRequest request, PostReceiptWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await workflow.PostAsync(
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);

        accounting.MapPost(
            "/receipts/{documentId:guid}/inventory-valuation/refresh",
            async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptInventoryValuationStore valuationStore, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await valuationStore.RefreshReceiptValuationAsync(
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);

        accounting.MapPost(
            "/receipts/{documentId:guid}/inventory-cost-layer-emission/emit",
            async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptInventoryCostLayerEmissionStore emissionStore, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await emissionStore.EmitReceiptCostLayersAsync(
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);

        accounting.MapPost(
            "/receipts/{documentId:guid}/grir-bridge/refresh",
            async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrBridgeStore grIrBridgeStore, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);

        accounting.MapPost(
            "/receipts/{documentId:guid}/grir-settlement/refresh",
            async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrApSettlementControlStore grIrSettlementStore, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await grIrSettlementStore.RefreshReceiptSettlementControlAsync(
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);

        accounting.MapPost(
            "/receipts/{documentId:guid}/grir-settlement/journal-reconciliation/refresh",
            async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrApSettlementControlStore grIrSettlementStore, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await grIrSettlementStore.RefreshReceiptSettlementJournalReconciliationAsync(
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);

        accounting.MapPost(
            "/receipts/{documentId:guid}/grir-settlement/purchase-variance/refresh",
            async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrApSettlementControlStore grIrSettlementStore, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await grIrSettlementStore.RefreshReceiptSettlementVarianceControlAsync(
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
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);

        accounting.MapGet(
            "/receipts/{documentId:guid}/grir-settlement/purchase-variance/lines",
            async (
                Guid documentId,
                [AsParameters] ReceiptLookupQuery query,
                IReceiptGrIrApSettlementControlStore grIrSettlementStore,
                CancellationToken cancellationToken) =>
            {
                var result = await grIrSettlementStore.ListReceiptPurchaseVarianceLinesAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                return Results.Ok(result);
            });

        accounting.MapGet(
            "/receipts/{documentId:guid}/grir-settlement/batches",
            async (
                Guid documentId,
                [AsParameters] ReceiptLookupQuery query,
                IReceiptGrIrApSettlementControlStore grIrSettlementStore,
                CancellationToken cancellationToken) =>
            {
                var result = await grIrSettlementStore.ListReceiptSettlementBatchesAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                return Results.Ok(result);
            });

        accounting.MapPost(
            "/receipts/{documentId:guid}/grir-settlement/execute",
            async (
                Guid documentId,
                ExecuteReceiptGrIrSettlementHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ExecuteReceiptGrIrSettlementCommandHandler handler,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequireGrIrSettlementExecutionAuthority(
                    sessionAccessor.Current,
                    "execute");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await handler.HandleAsync(
                        new ExecuteReceiptGrIrSettlementCommand(
                            request.CompanyId,
                            request.UserId,
                            documentId,
                            request.SettlementAmountBase,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/receipts/{documentId:guid}/grir-settlement/{settlementBatchId:guid}/journal/post",
            async (
                Guid documentId,
                Guid settlementBatchId,
                PostReceiptGrIrSettlementJournalHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                PostReceiptGrIrSettlementJournalCommandHandler handler,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequireGrIrSettlementExecutionAuthority(
                    sessionAccessor.Current,
                    "post");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await handler.HandleAsync(
                        new PostReceiptGrIrSettlementJournalCommand(
                            request.CompanyId,
                            request.UserId,
                            documentId,
                            settlementBatchId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/receipts/{documentId:guid}/grir-settlement/{settlementBatchId:guid}/ap-open-item/clear",
            async (
                Guid documentId,
                Guid settlementBatchId,
                PostReceiptDraftHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ClearReceiptGrIrSettlementOpenItemCommandHandler handler,
                CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequireGrIrSettlementExecutionAuthority(
                    sessionAccessor.Current,
                    "clear");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await handler.HandleAsync(
                        new ClearReceiptGrIrSettlementOpenItemCommand(
                            request.CompanyId,
                            request.UserId,
                            documentId,
                            settlementBatchId),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/receipts/{documentId:guid}/grir-settlement/{settlementBatchId:guid}/ap-open-item/reverse",
            async (
                Guid documentId,
                Guid settlementBatchId,
                PostReceiptDraftHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler handler,
                CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequireGrIrSettlementExecutionAuthority(
                    sessionAccessor.Current,
                    "reverse");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var result = await handler.HandleAsync(
                        new ReverseReceiptGrIrSettlementOpenItemClearingCommand(
                            request.CompanyId,
                            request.UserId,
                            documentId,
                            settlementBatchId),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/receipts/{documentId:guid}/grir-bridge/post",
            async (Guid documentId, PostReceiptGrIrBridgeHttpRequest request, PostReceiptGrIrCommandHandler handler, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostReceiptGrIrCommand(
                            request.CompanyId,
                            request.UserId,
                            documentId,
                            request.GrIrClearingAccountId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.InventoryStockAdjust);

        // -----------------------------------------------------------------------
        // M3 — Sales Issue → COGS posting bridge (Inventory V1 plan).
        // Operator-triggered (UI button on the Sales Issue review page, future
        // auto-trigger from Invoice post when the operator opts in). Idempotent
        // at the journal layer: re-running on the same sales-issue returns the
        // existing JE rather than double-posting.
        //
        // Active company id + user id come from the BusinessSession header
        // (matches the modern endpoint convention; the GR/IR endpoint above
        // pre-dates that pattern and will migrate later).
        // -----------------------------------------------------------------------
        // M3 iter 2 — workbench listing for posted sales-issues + their COGS
        // bridge state. LEFT JOIN to journal_entries shows already-posted vs
        // eligible without a persisted bridge table; that may come later if
        // per-line slice tracking becomes a requirement.
        accounting.MapGet(
            "/sales-issues/cogs-status",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                ISalesIssueCogsStatusReader reader,
                int? take,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await reader.ListAsync(session.ActiveCompanyId, take ?? 100, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapPost(
            "/sales-issues/{documentId:guid}/cogs/post",
            async (
                Guid documentId,
                BusinessSessionContextAccessor sessionAccessor,
                PostSalesIssueCogsCommandHandler handler,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrEmpty(session.UserId.Value)) return Results.Unauthorized();

                try
                {
                    var result = await handler.HandleAsync(
                        new PostSalesIssueCogsCommand(
                            session.ActiveCompanyId,
                            session.UserId,
                            documentId),
                        cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            });

        accounting.MapGet(
            "/vendor-credits/drafts/{documentId:guid}",
            async (Guid documentId, [AsParameters] VendorCreditLookupQuery query, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(query.CompanyId, documentId, cancellationToken);
                return document is null || document.Status != "draft"
                    ? Results.NotFound(new { message = "Vendor credit draft was not found in the active company context." })
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
                        Lines = document.VendorCreditLines.Select(line => new
                        {
                            line.LineNumber,
                            line.ExpenseAccountId,
                            line.Description,
                            line.LineAmount,
                            line.TaxCodeId,
                            line.TaxAmount,
                            line.IsTaxRecoverable
                        })
                    });
            });

        accounting.MapPost(
            "/vendor-credits/drafts",
            async (SaveVendorCreditDraftHttpRequest request, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.SaveDraftAsync(
                        new VendorCreditDraftSaveModel(
                            null,
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.VendorCreditDate,
                            request.DueDate,
                            request.TransactionCurrencyCode,
                            request.BaseCurrencyCode,
                            request.FxSnapshotId,
                            request.FxRate,
                            request.FxEffectiveDate,
                            request.FxSource,
                            request.Memo,
                            request.Lines.Select(static line => new VendorCreditDraftLineSaveModel(
                                line.LineNumber,
                                line.ExpenseAccountId,
                                line.Description,
                                line.LineAmount,
                                line.TaxCodeId,
                                line.TaxAmount,
                                line.IsTaxRecoverable)).ToArray()),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPut(
            "/vendor-credits/drafts/{documentId:guid}",
            async (Guid documentId, SaveVendorCreditDraftHttpRequest request, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.SaveDraftAsync(
                        new VendorCreditDraftSaveModel(
                            documentId,
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.VendorCreditDate,
                            request.DueDate,
                            request.TransactionCurrencyCode,
                            request.BaseCurrencyCode,
                            request.FxSnapshotId,
                            request.FxRate,
                            request.FxEffectiveDate,
                            request.FxSource,
                            request.Memo,
                            request.Lines.Select(static line => new VendorCreditDraftLineSaveModel(
                                line.LineNumber,
                                line.ExpenseAccountId,
                                line.Description,
                                line.LineAmount,
                                line.TaxCodeId,
                                line.TaxAmount,
                                line.IsTaxRecoverable)).ToArray()),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapGet(
            "/vendor-credits/{documentId:guid}",
            async (Guid documentId, [AsParameters] VendorCreditLookupQuery query, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                if (document is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Vendor credit document was not found in the active company context."
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
                    VendorId = document.PartyId,
                    PayableAccountId = document.PayableAccountId,
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    document.SubtotalAmount,
                    document.TaxAmount,
                    document.TotalAmount,
                    document.Memo,
                    Lines = document.VendorCreditLines.Select(line => new
                    {
                        line.LineNumber,
                        line.ExpenseAccountId,
                        line.Description,
                        line.LineAmount,
                        line.TaxAmount,
                        line.IsTaxRecoverable,
                        line.RecoverableTaxAccountId
                    })
                });
            });

        accounting.MapPost(
            "/vendor-credits/{documentId:guid}/post",
            async (Guid documentId, PostVendorCreditHttpRequest request, PostVendorCreditCommandHandler handler, IUnitySearchProjectionStore unitySearchProjectionStore, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostVendorCreditCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-b: vendor-credit status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorCreditPost);
    }
}
