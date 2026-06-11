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
/// SearchDashboardFxManualJournal endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class SearchDashboardFxManualJournalEndpoints
{
    public static void MapSearchDashboardFxManualJournalEndpoints(this RouteGroupBuilder accounting)
    {

        accounting.MapPost(
            "/unitysearch/usage",
            async (
                UnitysearchUsageHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IUnitysearchEventStore eventStore,
                IUnitysearchUsageStatStore usageStore,
                IUnitysearchPairStatStore pairStore,
                IUnitysearchRecentQueryStore recentQueries,
                Citus.Modules.UnitySearch.Application.Contracts.IUnitySearchQueryClassPriorStore queryClassPriors,
                UnityAiFeatureFlagAccessor flags,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                var logger = loggerFactory.CreateLogger("unitysearch.usage");

                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
                {
                    return Results.Unauthorized();
                }
                if (!string.IsNullOrEmpty(request.CompanyId.Value) && !request.CompanyId.Equals(session.ActiveCompanyId))
                {
                    return Results.BadRequest(new { message = "company_id mismatch" });
                }
                if (string.IsNullOrWhiteSpace(request.Context) || string.IsNullOrWhiteSpace(request.EntityType) || string.IsNullOrWhiteSpace(request.EventType))
                {
                    return Results.BadRequest(new { message = "context, entity_type, and event_type are required" });
                }

                if (!flags.UnitysearchLearningEnabled)
                {
                    return Results.Ok(new { ok = true, learning = "disabled" });
                }

                var companyId = session.ActiveCompanyId;
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var now = DateTimeOffset.UtcNow;
                var normalizedQuery = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim().ToLowerInvariant();

                try
                {
                    await eventStore.RecordEventAsync(new UnitysearchEventInput(
                        CompanyId: companyId,
                        UserId: userId,
                        SessionId: request.SessionId,
                        Context: request.Context.Trim(),
                        EntityType: request.EntityType.Trim(),
                        Query: request.Query,
                        NormalizedQuery: normalizedQuery,
                        EventType: request.EventType.Trim(),
                        SelectedEntityId: request.SelectedEntityId,
                        RankPosition: request.RankPosition,
                        ResultCount: request.ResultCount,
                        SourceRoute: request.SourceRoute,
                        AnchorContext: request.AnchorContext,
                        AnchorEntityType: request.AnchorEntityType,
                        AnchorEntityId: request.AnchorEntityId,
                        MetadataJson: request.MetadataJson), cancellationToken);

                    if (string.Equals(request.EventType, UnitysearchEventType.Select, StringComparison.OrdinalIgnoreCase) && request.SelectedEntityId.HasValue)
                    {
                        await usageStore.UpsertOnSelectAsync(
                            companyId, userId, request.Context, request.EntityType, request.SelectedEntityId.Value,
                            request.RankPosition, request.Query, now, cancellationToken);

                        if (!string.IsNullOrWhiteSpace(request.AnchorContext) &&
                            !string.IsNullOrWhiteSpace(request.AnchorEntityType) &&
                            request.AnchorEntityId.HasValue)
                        {
                            await pairStore.UpsertOnSelectAsync(
                                companyId, userId,
                                request.AnchorContext!, request.AnchorEntityType!, request.AnchorEntityId.Value,
                                request.Context, request.EntityType, request.SelectedEntityId.Value,
                                now, cancellationToken);
                        }

                        if (!string.IsNullOrWhiteSpace(request.Query) && !string.IsNullOrWhiteSpace(normalizedQuery))
                        {
                            await recentQueries.RecordAsync(
                                companyId, userId, request.Context, request.Query, normalizedQuery,
                                resultClicked: true,
                                clickedEntityType: request.EntityType,
                                clickedEntityId: request.SelectedEntityId,
                                resultCount: request.ResultCount,
                                createdAt: now,
                                cancellationToken);
                        }

                        // Per-user query-class prior for the next search ranker.
                        // Skips empty/text classes inside the store; numeric/code
                        // selections are what move the needle.
                        if (userId.HasValue)
                        {
                            var classification = Citus.Modules.UnitySearch.Application.UnitySearchQueryClassifier.Classify(normalizedQuery);
                            await queryClassPriors.RecordSelectAsync(
                                companyId,
                                userId.Value,
                                classification.Tag,
                                request.EntityType.Trim(),
                                cancellationToken);
                        }
                    }

                    return Results.Ok(new { ok = true });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "unitysearch usage tracking failed (context={Context})", request.Context);
                    return Results.Ok(new { ok = false, error = "tracking_failed" });
                }
            });

        accounting.MapPost(
            "/reports/usage",
            async (
                ReportUsageHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IReportUsageEventStore eventStore,
                IReportUsageStatStore statStore,
                UnityAiFeatureFlagAccessor flags,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                var logger = loggerFactory.CreateLogger("reports.usage");

                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
                {
                    return Results.Unauthorized();
                }
                if (string.IsNullOrWhiteSpace(request.ReportKey) || string.IsNullOrWhiteSpace(request.EventType))
                {
                    return Results.BadRequest(new { message = "report_key and event_type are required" });
                }

                if (!flags.ReportUsageLearningEnabled)
                {
                    return Results.Ok(new { ok = true, learning = "disabled" });
                }

                var companyId = session.ActiveCompanyId;
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var input = new ReportUsageEventInput(
                    CompanyId: companyId,
                    UserId: userId,
                    ReportKey: request.ReportKey.Trim(),
                    EventType: request.EventType.Trim(),
                    DateRangeKey: request.DateRangeKey,
                    FiltersJson: request.FiltersJson,
                    SourceRoute: request.SourceRoute,
                    MetadataJson: request.MetadataJson);

                try
                {
                    await eventStore.RecordAsync(input, cancellationToken);
                    await statStore.UpsertAsync(input, DateTimeOffset.UtcNow, cancellationToken);
                    return Results.Ok(new { ok = true });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "report usage tracking failed (report={ReportKey})", request.ReportKey);
                    return Results.Ok(new { ok = false, error = "tracking_failed" });
                }
            });

        accounting.MapGet(
            "/dashboard/suggestions",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IDashboardWidgetSuggestionStore store,
                string? status,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var items = await store.GetForUserAsync(session.ActiveCompanyId, userId, status, cancellationToken);
                return Results.Ok(items);
            });

        accounting.MapPost(
            "/dashboard/suggestions/generate",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IDashboardSuggestionService service,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var now = DateTimeOffset.UtcNow;
                var result = await service.GenerateAsync(session.ActiveCompanyId, userId, now.AddDays(-30), now, cancellationToken);
                return Results.Ok(result);
            });

        accounting.MapPost(
            "/dashboard/suggestions/{id:guid}/accept",
            async (
                Guid id,
                BusinessSessionContextAccessor sessionAccessor,
                IDashboardWidgetSuggestionStore store,
                IDashboardUserWidgetStore widgetStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var existing = await store.GetByIdAsync(session.ActiveCompanyId, id, cancellationToken);
                if (existing is null) return Results.NotFound();

                var now = DateTimeOffset.UtcNow;
                await store.UpdateStatusAsync(id, DashboardSuggestionStatus.Accepted, now, null, null, cancellationToken);

                await widgetStore.UpsertAsync(new DashboardUserWidgetRecord(
                    Id: Guid.NewGuid(),
                    CompanyId: existing.CompanyId,
                    UserId: existing.UserId,
                    WidgetKey: existing.WidgetKey,
                    Title: existing.Title,
                    ConfigJson: null,
                    Position: null,
                    Source: DashboardWidgetSource.Suggestion,
                    Active: true,
                    CreatedAt: now,
                    UpdatedAt: now), cancellationToken);

                return Results.Ok(new { ok = true });
            });

        accounting.MapPost(
            "/dashboard/suggestions/{id:guid}/dismiss",
            async (
                Guid id,
                BusinessSessionContextAccessor sessionAccessor,
                IDashboardWidgetSuggestionStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var existing = await store.GetByIdAsync(session.ActiveCompanyId, id, cancellationToken);
                if (existing is null) return Results.NotFound();

                await store.UpdateStatusAsync(id, DashboardSuggestionStatus.Dismissed, null, DateTimeOffset.UtcNow, null, cancellationToken);
                return Results.Ok(new { ok = true });
            });

        accounting.MapPost(
            "/dashboard/suggestions/{id:guid}/snooze",
            async (
                Guid id,
                DashboardSnoozeHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IDashboardWidgetSuggestionStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var existing = await store.GetByIdAsync(session.ActiveCompanyId, id, cancellationToken);
                if (existing is null) return Results.NotFound();

                var until = request.SnoozedUntil ?? DateTimeOffset.UtcNow.AddDays(7);
                await store.UpdateStatusAsync(id, DashboardSuggestionStatus.Snoozed, null, null, until, cancellationToken);
                return Results.Ok(new { ok = true });
            });

        accounting.MapGet(
            "/action-center/tasks",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IActionCenterTaskService service,
                string? status,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var statusFilter = string.IsNullOrWhiteSpace(status) ? null : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var tasks = await service.GetTasksAsync(session.ActiveCompanyId, userId, statusFilter, cancellationToken);
                return Results.Ok(tasks);
            });

        accounting.MapPost(
            "/action-center/regenerate",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                IActionCenterTaskService service,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var result = await service.RegenerateAsync(session.ActiveCompanyId, userId, cancellationToken);
                return Results.Ok(result);
            });

        accounting.MapPost(
            "/action-center/tasks/{id:guid}/start",
            async (Guid id, BusinessSessionContextAccessor sessionAccessor, IActionCenterTaskService service, CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var updated = await service.StartAsync(session.ActiveCompanyId, id, userId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            });

        accounting.MapPost(
            "/action-center/tasks/{id:guid}/done",
            async (Guid id, BusinessSessionContextAccessor sessionAccessor, IActionCenterTaskService service, CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var updated = await service.CompleteAsync(session.ActiveCompanyId, id, userId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            });

        accounting.MapPost(
            "/action-center/tasks/{id:guid}/dismiss",
            async (Guid id, BusinessSessionContextAccessor sessionAccessor, IActionCenterTaskService service, CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var updated = await service.DismissAsync(session.ActiveCompanyId, id, userId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            });

        accounting.MapPost(
            "/action-center/tasks/{id:guid}/snooze",
            async (
                Guid id,
                ActionCenterSnoozeHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IActionCenterTaskService service,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                var userId = string.IsNullOrEmpty(session.UserId.Value) ? (UserId?)null : session.UserId;
                var until = request.SnoozedUntil ?? DateTimeOffset.UtcNow.AddDays(1);
                var updated = await service.SnoozeAsync(session.ActiveCompanyId, id, userId, until, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            });

        accounting.MapPost(
            "/fx-revaluation-batches/prepare",
            async (PrepareFxRevaluationBatchHttpRequest request, PrepareFxRevaluationBatchCommandHandler handler, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PrepareFxRevaluationBatchCommand(
                            request.CompanyId,
                            request.UserId,
                            request.BookId,
                            request.RevaluationDate,
                            new(request.TransactionCurrencyCode),
                            request.AcceptedFxSnapshotId,
                            request.IncludeAccountsReceivable,
                            request.IncludeAccountsPayable,
                            request.Memo),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new
                    {
                        message = ex.Message
                    });
                }
            });

        accounting.MapPost(
            "/fx-revaluation-batches/{documentId:guid}/prepare-next-period-unwind",
            async (Guid documentId, PrepareFxRevaluationUnwindBatchHttpRequest request, PrepareFxRevaluationUnwindBatchCommandHandler handler, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PrepareFxRevaluationUnwindBatchCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.UnwindDate,
                            request.Memo),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new
                    {
                        message = ex.Message
                    });
                }
            });

        accounting.MapGet(
            "/fx-revaluation-batches/{documentId:guid}/cascade-unwind-plan",
            async (Guid documentId, [AsParameters] FxRevaluationCascadeUnwindPlanQuery query, IFxRevaluationDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var plan = await repository.GetCascadeUnwindPlanAsync(
                        query.CompanyId,
                        documentId,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        plan.RequestedDocumentId,
                        plan.RequestedDisplayNumber,
                        plan.NextDocumentId,
                        plan.NextDisplayNumber,
                        plan.RequestedBatchIsTail,
                        ActiveRevaluationCount = plan.ActiveRevaluationChain.Count,
                        ActiveRevaluationChain = plan.ActiveRevaluationChain.Select(step => new
                        {
                            step.DocumentId,
                            step.DisplayNumber,
                            step.RevaluationDate,
                            step.PostedAt,
                            step.IsRequestedBatch,
                            step.IsNextStep
                        })
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new
                    {
                        message = ex.Message
                    });
                }
            });

        accounting.MapPost(
            "/fx-revaluation-batches/{documentId:guid}/prepare-cascade-unwind",
            async (Guid documentId, PrepareFxRevaluationUnwindBatchHttpRequest request, PrepareFxRevaluationCascadeUnwindBatchCommandHandler handler, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PrepareFxRevaluationCascadeUnwindBatchCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.UnwindDate,
                            request.Memo),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new
                    {
                        message = ex.Message
                    });
                }
            });

        accounting.MapPost(
            "/fx-revaluation-batches/{documentId:guid}/auto-post-cascade-unwind",
            async (Guid documentId, PrepareFxRevaluationUnwindBatchHttpRequest request, PostFxRevaluationCascadeUnwindCommandHandler handler, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostFxRevaluationCascadeUnwindCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.UnwindDate,
                            request.Memo,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new
                    {
                        message = ex.Message
                    });
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlJournalPost);

        accounting.MapGet(
            "/fx-revaluation-batches",
            async ([AsParameters] FxRevaluationBatchListQuery query, IFxRevaluationDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var batches = await repository.ListRecentAsync(
                    query.CompanyId,
                    query.Take ?? 50,
                    cancellationToken);

                return Results.Ok(batches.Select(batch => new
                {
                    batch.Id,
                    batch.EntityNumber,
                    batch.DisplayNumber,
                    batch.Status,
                    batch.BatchKind,
                    batch.ReversalOfDocumentId,
                    batch.BookId,
                    batch.BookCode,
                    batch.AccountingStandard,
                    batch.RevaluationProfile,
                    batch.FxRoundingPolicy,
                    batch.DocumentDate,
                    batch.TransactionCurrencyCode,
                    batch.BaseCurrencyCode,
                    batch.FxSnapshotId,
                    batch.FxRate,
                    batch.LineCount,
                    batch.UnrealizedTotalBase,
                    batch.LinkedJournalEntryId,
                    batch.LinkedJournalEntryDisplayNumber,
                    batch.LinkedJournalPostedAt,
                    batch.CreatedAt,
                    batch.UpdatedAt
                }));
            });

        accounting.MapGet(
            "/fx-revaluation-batches/{documentId:guid}",
            async (Guid documentId, [AsParameters] FxRevaluationBatchLookupQuery query, IFxRevaluationDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var document = await repository.GetForPostingAsync(
                        query.CompanyId,
                        documentId,
                        cancellationToken);

                    if (document is null)
                    {
                        return Results.NotFound(new
                        {
                            message = "FX revaluation batch was not found in the active company context."
                        });
                    }

                    return Results.Ok(new
                    {
                        document.Id,
                        CompanyId = document.CompanyId,
                        EntityNumber = document.EntityNumber.Value,
                        DisplayNumber = document.DisplayNumber.Value,
                        document.Status,
                        document.BatchKind,
                        document.ReversalOfDocumentId,
                        document.BookId,
                        document.BookCode,
                        document.AccountingStandard,
                        document.RevaluationProfile,
                        document.FxRoundingPolicy,
                        document.DocumentDate,
                        TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                        BaseCurrencyCode = document.BaseCurrencyCode.Value,
                        FxSnapshotId = document.FxSnapshot.SnapshotId == Guid.Empty ? (Guid?)null : document.FxSnapshot.SnapshotId,
                        FxRate = document.FxSnapshot.Rate,
                        FxRateType = document.FxSnapshot.RateType,
                        FxQuoteBasis = document.FxSnapshot.QuoteBasis,
                        FxRateUseCase = document.FxSnapshot.RateUseCase,
                        FxPostingReason = document.FxSnapshot.PostingReason,
                        FxRequestedDate = document.FxSnapshot.RequestedDate,
                        FxEffectiveDate = document.FxSnapshot.EffectiveDate,
                        FxSource = document.FxSnapshot.SourceSemantics,
                        document.UnrealizedFxGainAccountId,
                        document.UnrealizedFxLossAccountId,
                        document.Memo,
                        Lines = document.RevaluationLines.Select(line => new
                        {
                            line.LineNumber,
                            line.TargetOpenItemType,
                            line.TargetOpenItemId,
                            line.TargetBalanceSide,
                            line.TargetControlAccountId,
                            line.OffsetAccountId,
                            line.PartyId,
                            line.Description,
                            line.OpenAmountTx,
                            line.CarryingAmountBase,
                            line.RevaluedAmountBase,
                            line.UnrealizedAmountBase
                        })
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new
                    {
                        message = ex.Message
                    });
                }
            });

        accounting.MapPost(
            "/fx-revaluation-batches/{documentId:guid}/post",
            async (Guid documentId, PostFxRevaluationBatchHttpRequest request, PostFxRevaluationBatchCommandHandler handler, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostFxRevaluationBatchCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlJournalPost);

        accounting.MapGet(
            "/manual-journals/{documentId:guid}",
            async (Guid documentId, [AsParameters] ManualJournalLookupQuery query, IManualJournalDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                if (document is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Manual journal document was not found in the active company context."
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
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    document.Memo,
                    Lines = document.JournalLines.Select(line => new
                    {
                        line.LineNumber,
                        line.AccountId,
                        line.Description,
                        line.TxDebit,
                        line.TxCredit
                    })
                });
            });

        accounting.MapPost(
            "/manual-journals/{documentId:guid}/post",
            async (Guid documentId, PostManualJournalHttpRequest request, PostManualJournalCommandHandler handler, IUnitySearchProjectionStore unitySearchProjectionStore, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostManualJournalCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    // H15-b: manual journal status flipped draft → posted.
                    await unitySearchProjectionStore.InvalidateAsync(request.CompanyId, cancellationToken);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlJournalPost);

        // Single-shot save + post for the New Journal Entry form. Builds a
        // JournalEntryDraft from the wire payload, looks up each line's account
        // to verify it exists in the company / allows manual posting, then hands
        // off to JournalEntryWorkflow.PostDraftAsync which (a) saves the draft,
        // (b) resolves an FX snapshot when the entry is foreign-currency, and
        // (c) posts via the PostingStore. Atomic: a failure at any step rolls
        // back the whole submit.
        //
        // Note: the displayNumber the form previews is informational only —
        // the posting store always reserves a fresh number from the sequence
        // at post time. Honoring user overrides would require extending the
        // PostingStore signature; deferred until there's a real demand for it.
        accounting.MapPost(
            "/manual-journals/save-and-post",
            async (
                ManualJournalSaveAndPostHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IAccountStore accountStore,
                global::Modules.GL.JournalEntry.IJournalEntryWorkflow workflow,
                IUnitySearchProjectionStore unitySearchProjectionStore,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value) || string.IsNullOrEmpty(session.UserId.Value))
                {
                    return Results.Unauthorized();
                }

                if (request.Lines is null || request.Lines.Count == 0)
                {
                    return Results.BadRequest(new { message = "At least one journal line is required." });
                }

                var companyId = session.ActiveCompanyId;
                var baseCurrencyCode = string.IsNullOrWhiteSpace(request.BaseCurrencyCode)
                    ? request.TransactionCurrencyCode
                    : request.BaseCurrencyCode;

                var draft = new global::Modules.GL.JournalEntry.JournalEntryDraft
                {
                    CompanyId = companyId,
                    JournalDate = request.Date,
                    CurrencyCode = request.TransactionCurrencyCode.Trim().ToUpperInvariant(),
                    BaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant(),
                    Memo = request.Description ?? string.Empty,
                };

                var isForeignCurrency = !string.Equals(draft.CurrencyCode, draft.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase);
                if (isForeignCurrency)
                {
                    if (request.ExchangeRate is not { } rate || rate <= 0m)
                    {
                        return Results.BadRequest(new { message = "An exchange rate is required for foreign-currency journal entries." });
                    }
                    draft.FxRate = rate;
                    draft.FxSourceSemantics = SharedKernel.FX.FxSourceSemantics.Manual;
                    // FxEffectiveDate / FxSnapshotId stay default — the workflow's
                    // EnsureGovernedFxSnapshotAsync persists a manual snapshot when
                    // semantics are Manual without an existing snapshot.
                }
                else
                {
                    draft.FxRate = 1m;
                    draft.FxSourceSemantics = SharedKernel.FX.FxSourceSemantics.Identity;
                }

                // Resolve each line's account once. The frontend AccountPicker
                // already filters to is_active + allow_manual_posting, but the
                // backend re-verifies so a tampered request can't sneak past.
                var lineNumber = 0;
                foreach (var lineRequest in request.Lines)
                {
                    lineNumber++;
                    if (lineRequest.AccountId == Guid.Empty)
                    {
                        return Results.BadRequest(new { message = $"Line {lineNumber} is missing an account." });
                    }

                    var account = await accountStore.GetByIdAsync(companyId, lineRequest.AccountId, cancellationToken);
                    if (account is null)
                    {
                        return Results.BadRequest(new { message = $"Line {lineNumber} references an account that does not exist in the active company." });
                    }
                    if (!account.IsActive || !account.AllowManualPosting)
                    {
                        return Results.BadRequest(new { message = $"Line {lineNumber} references an account that is inactive or not allowed for manual posting." });
                    }

                    // Per-line counterparty is persisted as party_id + party_type.
                    // Only set the type when an id is present; the JE NamePicker
                    // sends "customer" or "vendor", with "customer" as the
                    // fallback for older clients that omit the kind.
                    Guid? partyId = lineRequest.CounterpartyId is { } cid && cid != Guid.Empty ? cid : null;
                    string partyType = partyId is null
                        ? string.Empty
                        : (string.IsNullOrWhiteSpace(lineRequest.CounterpartyType)
                            ? "customer"
                            : lineRequest.CounterpartyType.Trim().ToLowerInvariant());
                    Guid? taxCodeId = lineRequest.TaxCodeId is { } tid && tid != Guid.Empty ? tid : null;

                    draft.Lines.Add(new global::Modules.GL.JournalEntry.JournalEntryDraftLine
                    {
                        LineNumber = lineNumber,
                        Account = new global::Modules.GL.JournalEntry.JournalEntryAccountOption
                        {
                            AccountId = account.Id,
                            Code = account.Code,
                            Name = account.Name,
                            RootType = account.RootType,
                            DetailType = account.DetailType ?? string.Empty,
                            TypeLabel = account.RootType,
                            CurrencyCode = account.CurrencyCode ?? draft.BaseCurrencyCode,
                            AllowManualPosting = account.AllowManualPosting,
                        },
                        DebitAmount = lineRequest.Debit > 0m ? lineRequest.Debit : null,
                        CreditAmount = lineRequest.Credit > 0m ? lineRequest.Credit : null,
                        Description = lineRequest.Description ?? string.Empty,
                        PartyId = partyId,
                        PartyType = partyType,
                        TaxCodeId = taxCodeId,
                    });
                }

                try
                {
                    var result = await workflow.PostDraftAsync(draft, session.UserId, cancellationToken);

                    // H15-c: composite save-and-post created a new manual journal in
                    // 'posted' status — drop the UnitySearch projection so the journal
                    // shows up in topbar search + GL pickers without the 5-min wait.
                    await unitySearchProjectionStore.InvalidateAsync(companyId, cancellationToken);

                    return Results.Ok(new
                    {
                        documentId = result.DocumentId,
                        documentNumber = result.DocumentNumber,
                        journalEntryId = result.JournalEntryId,
                        journalDisplayNumber = result.JournalDisplayNumber,
                    });
                }
                catch (global::Modules.GL.JournalEntry.JournalEntryWorkflowException ex)
                {
                    return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.GlJournalPost);
    }
}
