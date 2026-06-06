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
/// CompanyBookAndOpenItem endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class CompanyBookAndOpenItemEndpoints
{
    public static void MapCompanyBookAndOpenItemEndpoints(this RouteGroupBuilder accounting)
    {

        accounting.MapGet(
            "/company-books",
            async ([AsParameters] CompanyBookGovernanceLookupQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var results = await workflow.ListBookGovernanceAsync(
                    query.CompanyId,
                    asOfDate,
                    cancellationToken);

                return Results.Ok(new
                {
                    AsOfDate = asOfDate,
                    Books = results.Select(result => new
                    {
                        Book = new
                        {
                            result.Book.BookId,
                            result.Book.CompanyId,
                            result.Book.BookCode,
                            result.Book.BookName,
                            result.Book.BookRole,
                            result.Book.AccountingStandard,
                            result.Book.BookBaseCurrencyCode,
                            result.Book.FunctionalCurrencyCode,
                            result.Book.PresentationCurrencyCode,
                            result.Book.IsPrimary,
                            result.Book.IsAdjustmentOnly,
                            result.Book.EffectiveFrom,
                            result.Book.IsActive
                        },
                        RemeasurementPolicy = result.RemeasurementPolicy is null
                            ? null
                            : new
                            {
                                result.RemeasurementPolicy.PolicyId,
                                result.RemeasurementPolicy.CompanyId,
                                result.RemeasurementPolicy.BookId,
                                result.RemeasurementPolicy.RateType,
                                result.RemeasurementPolicy.QuoteBasis,
                                result.RemeasurementPolicy.RateUseCase,
                                result.RemeasurementPolicy.PostingReason,
                                result.RemeasurementPolicy.RevaluationProfile,
                                result.RemeasurementPolicy.FxRoundingPolicy,
                                result.RemeasurementPolicy.EffectiveFrom,
                                result.RemeasurementPolicy.IsActive
                            },
                        MigrationEligibility = new
                        {
                            result.MigrationEligibility.ChangeMode,
                            result.MigrationEligibility.EvaluationBasis,
                            result.MigrationEligibility.HasCompanyPostedHistory,
                            result.MigrationEligibility.HasBookSpecificRevaluationHistory,
                            result.MigrationEligibility.DirectEditAllowed,
                            result.MigrationEligibility.Reason
                        },
                        GovernanceSignals = new
                        {
                            result.GovernanceSignals.HasClosedPeriods,
                            result.GovernanceSignals.HasIssuedReports,
                            result.GovernanceSignals.HasFiledTax,
                            Signals = result.GovernanceSignals.Signals.Select(signal => new
                            {
                                signal.SignalId,
                                signal.CompanyId,
                                signal.BookId,
                                signal.SignalType,
                                signal.SignalDate,
                                signal.ReferenceLabel,
                                signal.Notes,
                                signal.CreatedByUserId,
                                signal.CreatedAt
                            })
                        }
                    })
                });
            });

        accounting.MapGet(
            "/company-books/{bookId:guid}/governance-signals",
            async (Guid bookId, [AsParameters] CompanyBookGovernanceSignalsLookupQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var result = await workflow.GetGovernanceSignalsAsync(
                    query.CompanyId,
                    bookId,
                    asOfDate,
                    cancellationToken);

                return Results.Ok(new
                {
                    BookId = bookId,
                    AsOfDate = asOfDate,
                    result.HasClosedPeriods,
                    result.HasIssuedReports,
                    result.HasFiledTax,
                    Signals = result.Signals.Select(signal => new
                    {
                        signal.SignalId,
                        signal.CompanyId,
                        signal.BookId,
                        signal.SignalType,
                        signal.SignalDate,
                        signal.ReferenceLabel,
                        signal.Notes,
                        signal.CreatedByUserId,
                        signal.CreatedAt
                    })
                });
            });

        accounting.MapPost(
            "/company-books/{bookId:guid}/governance-signals",
            async (Guid bookId, CreateCompanyBookGovernanceSignalHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await workflow.CreateGovernanceSignalAsync(
                        request.CompanyId,
                        bookId,
                        request.SignalType,
                        request.SignalDate,
                        request.ReferenceLabel,
                        request.Notes,
                        request.UserId,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        Signal = new
                        {
                            result.Signal.SignalId,
                            result.Signal.CompanyId,
                            result.Signal.BookId,
                            result.Signal.SignalType,
                            result.Signal.SignalDate,
                            result.Signal.ReferenceLabel,
                            result.Signal.Notes,
                            result.Signal.CreatedByUserId,
                            result.Signal.CreatedAt
                        },
                        Summary = new
                        {
                            result.Summary.HasClosedPeriods,
                            result.Summary.HasIssuedReports,
                            result.Summary.HasFiledTax,
                            Signals = result.Summary.Signals.Select(signal => new
                            {
                                signal.SignalId,
                                signal.CompanyId,
                                signal.BookId,
                                signal.SignalType,
                                signal.SignalDate,
                                signal.ReferenceLabel,
                                signal.Notes,
                                signal.CreatedByUserId,
                                signal.CreatedAt
                            })
                        }
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/company-books/{bookId:guid}/close-periods",
            async (Guid bookId, RegisterCompanyBookClosedPeriodHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await workflow.RegisterClosedPeriodAsync(
                        request.CompanyId,
                        bookId,
                        request.PeriodEndDate,
                        request.ReferenceLabel,
                        request.Notes,
                        request.UserId,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        Signal = new
                        {
                            result.Signal.SignalId,
                            result.Signal.CompanyId,
                            result.Signal.BookId,
                            result.Signal.SignalType,
                            result.Signal.SignalDate,
                            result.Signal.ReferenceLabel,
                            result.Signal.Notes,
                            result.Signal.CreatedByUserId,
                            result.Signal.CreatedAt
                        },
                        Summary = new
                        {
                            result.Summary.HasClosedPeriods,
                            result.Summary.HasIssuedReports,
                            result.Summary.HasFiledTax,
                            Signals = result.Summary.Signals.Select(signal => new
                            {
                                signal.SignalId,
                                signal.CompanyId,
                                signal.BookId,
                                signal.SignalType,
                                signal.SignalDate,
                                signal.ReferenceLabel,
                                signal.Notes,
                                signal.CreatedByUserId,
                                signal.CreatedAt
                            })
                        }
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/company-books/{bookId:guid}/issued-statements",
            async (Guid bookId, RegisterCompanyBookIssuedStatementHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await workflow.RegisterIssuedStatementAsync(
                        request.CompanyId,
                        bookId,
                        request.IssuedOn,
                        request.StatementLabel,
                        request.Notes,
                        request.UserId,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        Signal = new
                        {
                            result.Signal.SignalId,
                            result.Signal.CompanyId,
                            result.Signal.BookId,
                            result.Signal.SignalType,
                            result.Signal.SignalDate,
                            result.Signal.ReferenceLabel,
                            result.Signal.Notes,
                            result.Signal.CreatedByUserId,
                            result.Signal.CreatedAt
                        },
                        Summary = new
                        {
                            result.Summary.HasClosedPeriods,
                            result.Summary.HasIssuedReports,
                            result.Summary.HasFiledTax,
                            Signals = result.Summary.Signals.Select(signal => new
                            {
                                signal.SignalId,
                                signal.CompanyId,
                                signal.BookId,
                                signal.SignalType,
                                signal.SignalDate,
                                signal.ReferenceLabel,
                                signal.Notes,
                                signal.CreatedByUserId,
                                signal.CreatedAt
                            })
                        }
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/company-books/{bookId:guid}/filed-tax",
            async (Guid bookId, RegisterCompanyBookFiledTaxHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await workflow.RegisterFiledTaxAsync(
                        request.CompanyId,
                        bookId,
                        request.FiledOn,
                        request.FilingLabel,
                        request.Notes,
                        request.UserId,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        Signal = new
                        {
                            result.Signal.SignalId,
                            result.Signal.CompanyId,
                            result.Signal.BookId,
                            result.Signal.SignalType,
                            result.Signal.SignalDate,
                            result.Signal.ReferenceLabel,
                            result.Signal.Notes,
                            result.Signal.CreatedByUserId,
                            result.Signal.CreatedAt
                        },
                        Summary = new
                        {
                            result.Summary.HasClosedPeriods,
                            result.Summary.HasIssuedReports,
                            result.Summary.HasFiledTax,
                            Signals = result.Summary.Signals.Select(signal => new
                            {
                                signal.SignalId,
                                signal.CompanyId,
                                signal.BookId,
                                signal.SignalType,
                                signal.SignalDate,
                                signal.ReferenceLabel,
                                signal.Notes,
                                signal.CreatedByUserId,
                                signal.CreatedAt
                            })
                        }
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/company-books/governed-change-preview",
            async (CompanyBookGovernedChangePreviewHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var asOfDate = request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                    var result = await workflow.PreviewGovernedChangeAsync(
                        request.CompanyId,
                        request.BookId,
                        asOfDate,
                        new CompanyBookProposedChangeSet(
                            request.IsPrimary,
                            request.AccountingStandard,
                            request.BookBaseCurrencyCode,
                            request.FunctionalCurrencyCode,
                            request.PresentationCurrencyCode,
                            request.RateType,
                            request.QuoteBasis,
                            request.RateUseCase,
                            request.PostingReason,
                            request.RevaluationProfile,
                            request.FxRoundingPolicy),
                        cancellationToken);

                    return Results.Ok(new
                    {
                        AsOfDate = asOfDate,
                        Book = new
                        {
                            result.Book.BookId,
                            result.Book.CompanyId,
                            result.Book.BookCode,
                            result.Book.BookName,
                            result.Book.BookRole,
                            result.Book.AccountingStandard,
                            result.Book.BookBaseCurrencyCode,
                            result.Book.FunctionalCurrencyCode,
                            result.Book.PresentationCurrencyCode,
                            result.Book.IsPrimary,
                            result.Book.IsAdjustmentOnly,
                            result.Book.EffectiveFrom,
                            result.Book.IsActive
                        },
                        CurrentRemeasurementPolicy = result.CurrentRemeasurementPolicy is null
                            ? null
                            : new
                            {
                                result.CurrentRemeasurementPolicy.PolicyId,
                                result.CurrentRemeasurementPolicy.CompanyId,
                                result.CurrentRemeasurementPolicy.BookId,
                                result.CurrentRemeasurementPolicy.RateType,
                                result.CurrentRemeasurementPolicy.QuoteBasis,
                                result.CurrentRemeasurementPolicy.RateUseCase,
                                result.CurrentRemeasurementPolicy.PostingReason,
                                result.CurrentRemeasurementPolicy.RevaluationProfile,
                                result.CurrentRemeasurementPolicy.FxRoundingPolicy,
                                result.CurrentRemeasurementPolicy.EffectiveFrom,
                                result.CurrentRemeasurementPolicy.IsActive
                            },
                        ProposedChanges = result.ProposedChanges,
                        ChangeImpact = new
                        {
                            result.ChangeImpact.HasAnyChange,
                            result.ChangeImpact.ChangedFields,
                            result.ChangeImpact.ChangeCategories,
                            result.ChangeImpact.DirectUpdateAllowed,
                            result.ChangeImpact.GovernedMigrationRequired,
                            result.ChangeImpact.RecommendedPath,
                            result.ChangeImpact.EvaluationBasis,
                            result.ChangeImpact.Reason
                        }
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
            "/company-books/governed-change-requests/prepare",
            async (PrepareCompanyBookGovernedChangeRequestHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var asOfDate = request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                    var result = await workflow.PrepareGovernedChangeRequestDraftAsync(
                        request.CompanyId,
                        request.UserId,
                        request.BookId,
                        asOfDate,
                        request.EffectiveFrom,
                        new CompanyBookProposedChangeSet(
                            request.IsPrimary,
                            request.AccountingStandard,
                            request.BookBaseCurrencyCode,
                            request.FunctionalCurrencyCode,
                            request.PresentationCurrencyCode,
                            request.RateType,
                            request.QuoteBasis,
                            request.RateUseCase,
                            request.PostingReason,
                            request.RevaluationProfile,
                            request.FxRoundingPolicy),
                        cancellationToken);

                    return Results.Ok(new
                    {
                        result.RequestId,
                        result.CompanyId,
                        result.BookId,
                        result.Status,
                        result.RequestedAction,
                        result.AsOfDate,
                        result.EffectiveFrom,
                        result.CreatedByUserId,
                        result.CreatedAt,
                        result.SubmittedByUserId,
                        result.SubmittedAt,
                        result.CancelledByUserId,
                        result.CancelledAt,
                        result.AppliedAt,
                        Book = new
                        {
                            result.Preview.Book.BookId,
                            result.Preview.Book.CompanyId,
                            result.Preview.Book.BookCode,
                            result.Preview.Book.BookName,
                            result.Preview.Book.BookRole,
                            result.Preview.Book.AccountingStandard,
                            result.Preview.Book.BookBaseCurrencyCode,
                            result.Preview.Book.FunctionalCurrencyCode,
                            result.Preview.Book.PresentationCurrencyCode,
                            result.Preview.Book.IsPrimary,
                            result.Preview.Book.IsAdjustmentOnly,
                            result.Preview.Book.EffectiveFrom,
                            result.Preview.Book.IsActive
                        },
                        CurrentRemeasurementPolicy = result.Preview.CurrentRemeasurementPolicy is null
                            ? null
                            : new
                            {
                                result.Preview.CurrentRemeasurementPolicy.PolicyId,
                                result.Preview.CurrentRemeasurementPolicy.CompanyId,
                                result.Preview.CurrentRemeasurementPolicy.BookId,
                                result.Preview.CurrentRemeasurementPolicy.RateType,
                                result.Preview.CurrentRemeasurementPolicy.QuoteBasis,
                                result.Preview.CurrentRemeasurementPolicy.RateUseCase,
                                result.Preview.CurrentRemeasurementPolicy.PostingReason,
                                result.Preview.CurrentRemeasurementPolicy.RevaluationProfile,
                                result.Preview.CurrentRemeasurementPolicy.FxRoundingPolicy,
                                result.Preview.CurrentRemeasurementPolicy.EffectiveFrom,
                                result.Preview.CurrentRemeasurementPolicy.IsActive
                            },
                        result.Preview.ProposedChanges,
                        ChangeImpact = new
                        {
                            result.Preview.ChangeImpact.HasAnyChange,
                            result.Preview.ChangeImpact.ChangedFields,
                            result.Preview.ChangeImpact.ChangeCategories,
                            result.Preview.ChangeImpact.DirectUpdateAllowed,
                            result.Preview.ChangeImpact.GovernedMigrationRequired,
                            result.Preview.ChangeImpact.RecommendedPath,
                            result.Preview.ChangeImpact.EvaluationBasis,
                            result.Preview.ChangeImpact.Reason
                        }
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

        accounting.MapGet(
            "/company-books/governed-change-requests",
            async ([AsParameters] CompanyBookGovernedChangeRequestLookupQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                var results = await workflow.ListGovernedChangeRequestDraftsAsync(query.CompanyId, cancellationToken);

                return Results.Ok(new
                {
                    Requests = results.Select(result => new
                    {
                        result.RequestId,
                        result.CompanyId,
                        result.BookId,
                        result.Status,
                        result.RequestedAction,
                        result.AsOfDate,
                        result.EffectiveFrom,
                        result.CreatedByUserId,
                        result.CreatedAt,
                        result.SubmittedByUserId,
                        result.SubmittedAt,
                        result.CancelledByUserId,
                        result.CancelledAt,
                        result.AppliedAt,
                        Book = new
                        {
                            result.Preview.Book.BookId,
                            result.Preview.Book.CompanyId,
                            result.Preview.Book.BookCode,
                            result.Preview.Book.BookName,
                            result.Preview.Book.BookRole,
                            result.Preview.Book.AccountingStandard,
                            result.Preview.Book.BookBaseCurrencyCode,
                            result.Preview.Book.FunctionalCurrencyCode,
                            result.Preview.Book.PresentationCurrencyCode,
                            result.Preview.Book.IsPrimary,
                            result.Preview.Book.IsAdjustmentOnly,
                            result.Preview.Book.EffectiveFrom,
                            result.Preview.Book.IsActive
                        },
                        CurrentRemeasurementPolicy = result.Preview.CurrentRemeasurementPolicy is null
                            ? null
                            : new
                            {
                                result.Preview.CurrentRemeasurementPolicy.PolicyId,
                                result.Preview.CurrentRemeasurementPolicy.CompanyId,
                                result.Preview.CurrentRemeasurementPolicy.BookId,
                                result.Preview.CurrentRemeasurementPolicy.RateType,
                                result.Preview.CurrentRemeasurementPolicy.QuoteBasis,
                                result.Preview.CurrentRemeasurementPolicy.RateUseCase,
                                result.Preview.CurrentRemeasurementPolicy.PostingReason,
                                result.Preview.CurrentRemeasurementPolicy.RevaluationProfile,
                                result.Preview.CurrentRemeasurementPolicy.FxRoundingPolicy,
                                result.Preview.CurrentRemeasurementPolicy.EffectiveFrom,
                                result.Preview.CurrentRemeasurementPolicy.IsActive
                            },
                        result.Preview.ProposedChanges,
                        ChangeImpact = new
                        {
                            result.Preview.ChangeImpact.HasAnyChange,
                            result.Preview.ChangeImpact.ChangedFields,
                            result.Preview.ChangeImpact.ChangeCategories,
                            result.Preview.ChangeImpact.DirectUpdateAllowed,
                            result.Preview.ChangeImpact.GovernedMigrationRequired,
                            result.Preview.ChangeImpact.RecommendedPath,
                            result.Preview.ChangeImpact.EvaluationBasis,
                            result.Preview.ChangeImpact.Reason
                        }
                    })
                });
            });

        accounting.MapPost(
            "/company-books/governed-change-requests/{requestId:guid}/submit",
            async (Guid requestId, TransitionCompanyBookGovernedChangeRequestHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await workflow.SubmitGovernedChangeRequestDraftAsync(
                        request.CompanyId,
                        requestId,
                        request.UserId,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        result.RequestId,
                        result.CompanyId,
                        result.BookId,
                        result.Status,
                        result.RequestedAction,
                        result.AsOfDate,
                        result.EffectiveFrom,
                        result.CreatedByUserId,
                        result.CreatedAt,
                        result.SubmittedByUserId,
                        result.SubmittedAt,
                        result.CancelledByUserId,
                        result.CancelledAt,
                        result.AppliedAt
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
            "/company-books/governed-change-requests/{requestId:guid}/cancel",
            async (Guid requestId, TransitionCompanyBookGovernedChangeRequestHttpRequest request, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await workflow.CancelGovernedChangeRequestDraftAsync(
                        request.CompanyId,
                        requestId,
                        request.UserId,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        result.RequestId,
                        result.CompanyId,
                        result.BookId,
                        result.Status,
                        result.RequestedAction,
                        result.AsOfDate,
                        result.EffectiveFrom,
                        result.CreatedByUserId,
                        result.CreatedAt,
                        result.SubmittedByUserId,
                        result.SubmittedAt,
                        result.CancelledByUserId,
                        result.CancelledAt,
                        result.AppliedAt
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

        accounting.MapGet(
            "/company-books/governed-change-requests/{requestId:guid}/apply-readiness",
            async (Guid requestId, [AsParameters] CompanyBookGovernedChangeRequestReadinessQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                    var result = await workflow.ValidateGovernedChangeRequestApplyReadinessAsync(
                        query.CompanyId,
                        requestId,
                        asOfDate,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        result.RequestId,
                        result.Status,
                        result.EffectiveFrom,
                        result.EvaluatedAt,
                        result.CurrentTruthMatchesDraft,
                        result.IsReadyToApply,
                        result.RequiresNewBookRollout,
                        result.Blockers,
                        result.Warnings
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

        accounting.MapGet(
            "/company-books/remeasurement-policy",
            async ([AsParameters] CompanyBookPolicyLookupQuery query, ICompanyBookPolicyWorkflow workflow, CancellationToken cancellationToken) =>
            {
                try
                {
                    var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                    var result = await workflow.GetRemeasurementPolicyAsync(
                        query.CompanyId,
                        query.BookId,
                        asOfDate,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        AsOfDate = asOfDate,
                        result.WasProvisioned,
                        Book = new
                        {
                            result.Book.BookId,
                            result.Book.CompanyId,
                            result.Book.BookCode,
                            result.Book.BookName,
                            result.Book.BookRole,
                            result.Book.AccountingStandard,
                            result.Book.BookBaseCurrencyCode,
                            result.Book.FunctionalCurrencyCode,
                            result.Book.PresentationCurrencyCode,
                            result.Book.IsPrimary,
                            result.Book.IsAdjustmentOnly,
                            result.Book.EffectiveFrom,
                            result.Book.IsActive
                        },
                        RemeasurementPolicy = new
                        {
                            result.RemeasurementPolicy.PolicyId,
                            result.RemeasurementPolicy.CompanyId,
                            result.RemeasurementPolicy.BookId,
                            result.RemeasurementPolicy.RateType,
                            result.RemeasurementPolicy.QuoteBasis,
                            result.RemeasurementPolicy.RateUseCase,
                            result.RemeasurementPolicy.PostingReason,
                            result.RemeasurementPolicy.RevaluationProfile,
                            result.RemeasurementPolicy.FxRoundingPolicy,
                            result.RemeasurementPolicy.EffectiveFrom,
                            result.RemeasurementPolicy.IsActive
                        }
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

        accounting.MapGet(
            "/session/context",
            async (
                BusinessSessionContextAccessor accessor,
                IPlatformRuntimeStateRepository runtimeStateRepository,
                BusinessSessionDirectory sessionDirectory,
                CancellationToken cancellationToken) =>
            {
                var session = accessor.Current ??
                    throw new InvalidOperationException("Business session context was not resolved for the current request.");
                var maintenanceState = await runtimeStateRepository.GetMaintenanceStateAsync(cancellationToken);
                var resolution = accessor.CurrentResolution;
                if (resolution is null)
                {
                    var resolved = await sessionDirectory.ResolveAsync(session, cancellationToken);
                    if (!resolved.Success || resolved.Resolution is null)
                    {
                        return Results.Json(
                            new
                            {
                                message = resolved.Error ?? "Business session context could not be resolved for the current environment."
                            },
                            statusCode: StatusCodes.Status403Forbidden);
                    }

                    resolution = resolved.Resolution;
                }

                return Results.Ok(new BusinessSessionContextSummary
                {
                    User = resolution.User,
                    ActiveCompany = resolution.ActiveCompany,
                    AvailableCompanies = resolution.AvailableCompanies,
                    MaintenanceState = new MaintenanceStateSummary
                    {
                        Enabled = maintenanceState?.Enabled ?? false,
                        Message = maintenanceState?.Message ?? "Platform runtime is accepting interactive changes.",
                        ScheduledUntilUtc = maintenanceState?.ScheduledUntilUtc
                    }
                });
            });

        accounting.MapReportsEndpoints();

        accounting.MapGet(
            "/open-items/ar/{openItemId:guid}",
            async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IArOpenItemRepository openItemRepository, ISettlementApplicationRepository settlementRepository, CancellationToken cancellationToken) =>
            {
                var item = await openItemRepository.GetDrillDownAsync(
                    query.CompanyId,
                    openItemId,
                    cancellationToken);

                if (item is null)
                {
                    return Results.NotFound(new
                    {
                        message = "AR open item was not found in the active company context."
                    });
                }

                var applications = await settlementRepository.ListApplicationsAsync(
                    query.CompanyId,
                    "ar_open_item",
                    openItemId,
                    cancellationToken);

                return Results.Ok(new
                {
                    OpenItem = new
                    {
                        item.OpenItemId,
                        item.OpenItemType,
                        CompanyId = item.CompanyId,
                        item.PartyRole,
                        item.PartyId,
                        item.PartyEntityNumber,
                        item.PartyDisplayName,
                        item.SourceType,
                        item.SourceDocumentId,
                        item.SourceDocumentDisplayNumber,
                        item.DocumentDate,
                        item.DueDate,
                        item.DocumentCurrencyCode,
                        item.BaseCurrencyCode,
                        item.BalanceSide,
                        item.Status,
                        item.OriginalAmountTx,
                        item.OriginalAmountBase,
                        item.OpenAmountTx,
                        item.OpenAmountBase
                    },
                    Applications = applications.Select(application => new
                    {
                        application.ApplicationId,
                        application.ApplicationType,
                        application.SourceType,
                        application.SourceDocumentId,
                        application.SourceDocumentDisplayNumber,
                        application.SourceDocumentDate,
                        application.AppliedAmountTx,
                        application.AppliedAmountBase,
                        application.SettlementFxRate,
                        application.RealizedFxAmount,
                        application.CreatedAt
                    })
                });
            });

        accounting.MapGet(
            "/open-item-adjustment-account-mappings",
            async ([AsParameters] OpenItemAdjustmentAccountMappingLookupQuery query, IOpenItemAdjustmentAccountMappingRepository repository, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await repository.LookupAsync(
                        new OpenItemAdjustmentAccountMappingLookupRequest(
                            query.CompanyId,
                            query.OpenItemType,
                            query.AdjustmentType,
                            query.IncludeInactive == true,
                            query.BookId,
                            query.PolicyScope,
                            query.SearchText,
                            query.Limit ?? 200),
                        cancellationToken);

                    return Results.Ok(new
                    {
                        CompanyId = query.CompanyId,
                        OpenItemType = query.OpenItemType,
                        AdjustmentType = query.AdjustmentType,
                        IncludeInactive = query.IncludeInactive == true,
                        query.BookId,
                        query.PolicyScope,
                        query.SearchText,
                        Limit = Math.Clamp(query.Limit ?? 200, 1, 500),
                        Summary = result.Summary,
                        Mappings = result.Mappings
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/open-item-adjustment-account-mappings",
            async (SaveOpenItemAdjustmentAccountMappingHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IOpenItemAdjustmentAccountMappingRepository repository, CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequireOpenItemAdjustmentAccountMappingManagementAuthority(
                    sessionAccessor.Current,
                    "save");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                try
                {
                    var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                    var result = await repository.SaveAsync(
                        new OpenItemAdjustmentAccountMappingSaveRequest(
                            request.CompanyId,
                            request.BookId,
                            request.OpenItemType,
                            request.AdjustmentType,
                            request.AdjustmentAccountId,
                            actorId),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/open-item-adjustment-account-mappings/{mappingId:guid}/deactivate",
            async (Guid mappingId, DeactivateOpenItemAdjustmentAccountMappingHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IOpenItemAdjustmentAccountMappingRepository repository, CancellationToken cancellationToken) =>
            {
                var authorityBlock = RequireOpenItemAdjustmentAccountMappingManagementAuthority(
                    sessionAccessor.Current,
                    "deactivate");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var result = await repository.DeactivateAsync(
                    request.CompanyId,
                    mappingId,
                    actorId,
                    cancellationToken);

                return result is null
                    ? Results.NotFound(new { message = "Open-item adjustment account mapping was not found in the active company context." })
                    : Results.Ok(result);
            });

        accounting.MapGet(
            "/open-items/ar/{openItemId:guid}/adjustment-preview",
            async (Guid openItemId, [AsParameters] OpenItemAdjustmentPreviewLookupQuery query, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var adjustmentType = string.IsNullOrWhiteSpace(query.AdjustmentType) ? "write_off" : query.AdjustmentType;
                var adjustmentDate = query.AdjustmentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var preview = await openItemRepository.GetAdjustmentPreviewAsync(
                    query.CompanyId,
                    openItemId,
                    adjustmentType,
                    adjustmentDate,
                    query.AdjustmentAmountTx,
                    cancellationToken);

                return preview is null
                    ? Results.NotFound(new { message = "AR open item was not found in the active company context." })
                    : Results.Ok(preview);
            });

        accounting.MapPost(
            "/open-items/ar/{openItemId:guid}/adjustment-request",
            async (Guid openItemId, RequestOpenItemAdjustmentHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var adjustmentDate = request.AdjustmentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var attempt = await openItemRepository.RequestAdjustmentAsync(
                    request.CompanyId,
                    openItemId,
                    request.AdjustmentType,
                    adjustmentDate,
                    request.AdjustmentAmountTx,
                    actorId,
                    request.Reason,
                    cancellationToken);

                return attempt is null
                    ? Results.NotFound(new { message = "AR open item was not found in the active company context." })
                    : Results.Ok(attempt);
            });

        accounting.MapGet(
            "/open-items/ar/{openItemId:guid}/adjustment-request",
            async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var request = await openItemRepository.GetLatestAdjustmentRequestAsync(
                    query.CompanyId,
                    openItemId,
                    cancellationToken);

                return request is null
                    ? Results.NotFound(new { message = "No AR open item adjustment request was found for the active company context." })
                    : Results.Ok(request);
            });

        accounting.MapPost(
            "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/submit",
            async (Guid openItemId, Guid requestId, TransitionOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var result = await openItemRepository.SubmitAdjustmentRequestAsync(
                    request.CompanyId,
                    openItemId,
                    requestId,
                    actorId,
                    cancellationToken);

                return result is null
                    ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
                    : Results.Ok(result);
            });

        accounting.MapPost(
            "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/cancel",
            async (Guid openItemId, Guid requestId, TransitionOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var result = await openItemRepository.CancelAdjustmentRequestAsync(
                    request.CompanyId,
                    openItemId,
                    requestId,
                    actorId,
                    cancellationToken);

                return result is null
                    ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
                    : Results.Ok(result);
            });

        accounting.MapPost(
            "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/approve",
            async (Guid openItemId, Guid requestId, GovernOpenItemAdjustmentApprovalHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var companyId = CompanyId.Parse(request.CompanyId.ToString());
                var currentRequest = await openItemRepository.GetLatestAdjustmentRequestAsync(
                    companyId,
                    openItemId,
                    cancellationToken);

                if (currentRequest is null || currentRequest.RequestId != requestId)
                {
                    return Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." });
                }

                var authorityBlock = RequireOpenItemAdjustmentApprovalAuthority(
                    sessionAccessor.Current,
                    currentRequest,
                    "AR",
                    "approve");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var result = await openItemRepository.ApproveAdjustmentRequestAsync(
                    companyId,
                    openItemId,
                    requestId,
                    actorId,
                    cancellationToken);

                return result is null
                    ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
                    : Results.Ok(result);
            });

        accounting.MapPost(
            "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/reject",
            async (Guid openItemId, Guid requestId, GovernOpenItemAdjustmentApprovalHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var companyId = CompanyId.Parse(request.CompanyId.ToString());
                var currentRequest = await openItemRepository.GetLatestAdjustmentRequestAsync(
                    companyId,
                    openItemId,
                    cancellationToken);

                if (currentRequest is null || currentRequest.RequestId != requestId)
                {
                    return Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." });
                }

                var authorityBlock = RequireOpenItemAdjustmentApprovalAuthority(
                    sessionAccessor.Current,
                    currentRequest,
                    "AR",
                    "reject");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var result = await openItemRepository.RejectAdjustmentRequestAsync(
                    companyId,
                    openItemId,
                    requestId,
                    actorId,
                    cancellationToken);

                return result is null
                    ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
                    : Results.Ok(result);
            });

        accounting.MapGet(
            "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/readiness",
            async (Guid openItemId, Guid requestId, [AsParameters] OpenItemAdjustmentRequestReadinessQuery query, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var readiness = await openItemRepository.GetAdjustmentRequestReadinessAsync(
                    query.CompanyId,
                    openItemId,
                    requestId,
                    asOfDate,
                    cancellationToken);

                return readiness is null
                    ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
                    : Results.Ok(readiness);
            });

        accounting.MapGet(
            "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/execution-plan",
            async (Guid openItemId, Guid requestId, [AsParameters] OpenItemAdjustmentRequestReadinessQuery query, IArOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var plan = await openItemRepository.GetAdjustmentRequestExecutionPlanAsync(
                    query.CompanyId,
                    openItemId,
                    requestId,
                    asOfDate,
                    cancellationToken);

                return plan is null
                    ? Results.NotFound(new { message = "AR open item adjustment request was not found in the active company context." })
                    : Results.Ok(plan);
            });

        accounting.MapPost(
            "/open-items/ar/{openItemId:guid}/adjustment-request/{requestId:guid}/execute",
            async (Guid openItemId, Guid requestId, ExecuteOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, PostArOpenItemAdjustmentCommandHandler handler, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                if (!actorId.HasValue)
                {
                    return Results.BadRequest(new { message = "A user id is required to execute a governed AR open item adjustment." });
                }

                try
                {
                    var result = await handler.HandleAsync(
                        new PostArOpenItemAdjustmentCommand(
                            request.CompanyId,
                            openItemId,
                            requestId,
                            actorId.Value,
                            request.AdjustmentAccountId,
                            request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapGet(
            "/open-items/ap/{openItemId:guid}",
            async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IApOpenItemRepository openItemRepository, ISettlementApplicationRepository settlementRepository, CancellationToken cancellationToken) =>
            {
                var item = await openItemRepository.GetDrillDownAsync(
                    query.CompanyId,
                    openItemId,
                    cancellationToken);

                if (item is null)
                {
                    return Results.NotFound(new
                    {
                        message = "AP open item was not found in the active company context."
                    });
                }

                var applications = await settlementRepository.ListApplicationsAsync(
                    query.CompanyId,
                    "ap_open_item",
                    openItemId,
                    cancellationToken);

                return Results.Ok(new
                {
                    OpenItem = new
                    {
                        item.OpenItemId,
                        item.OpenItemType,
                        CompanyId = item.CompanyId,
                        item.PartyRole,
                        item.PartyId,
                        item.PartyEntityNumber,
                        item.PartyDisplayName,
                        item.SourceType,
                        item.SourceDocumentId,
                        item.SourceDocumentDisplayNumber,
                        item.DocumentDate,
                        item.DueDate,
                        item.DocumentCurrencyCode,
                        item.BaseCurrencyCode,
                        item.BalanceSide,
                        item.Status,
                        item.OriginalAmountTx,
                        item.OriginalAmountBase,
                        item.OpenAmountTx,
                        item.OpenAmountBase
                    },
                    Applications = applications.Select(application => new
                    {
                        application.ApplicationId,
                        application.ApplicationType,
                        application.SourceType,
                        application.SourceDocumentId,
                        application.SourceDocumentDisplayNumber,
                        application.SourceDocumentDate,
                        application.AppliedAmountTx,
                        application.AppliedAmountBase,
                        application.SettlementFxRate,
                        application.RealizedFxAmount,
                        application.CreatedAt
                    })
                });
            });

        accounting.MapGet(
            "/open-items/ap/{openItemId:guid}/adjustment-preview",
            async (Guid openItemId, [AsParameters] OpenItemAdjustmentPreviewLookupQuery query, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var adjustmentType = string.IsNullOrWhiteSpace(query.AdjustmentType) ? "small_balance_adjustment" : query.AdjustmentType;
                var adjustmentDate = query.AdjustmentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var preview = await openItemRepository.GetAdjustmentPreviewAsync(
                    query.CompanyId,
                    openItemId,
                    adjustmentType,
                    adjustmentDate,
                    query.AdjustmentAmountTx,
                    cancellationToken);

                return preview is null
                    ? Results.NotFound(new { message = "AP open item was not found in the active company context." })
                    : Results.Ok(preview);
            });

        accounting.MapPost(
            "/open-items/ap/{openItemId:guid}/adjustment-request",
            async (Guid openItemId, RequestOpenItemAdjustmentHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var adjustmentDate = request.AdjustmentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var attempt = await openItemRepository.RequestAdjustmentAsync(
                    request.CompanyId,
                    openItemId,
                    request.AdjustmentType,
                    adjustmentDate,
                    request.AdjustmentAmountTx,
                    actorId,
                    request.Reason,
                    cancellationToken);

                return attempt is null
                    ? Results.NotFound(new { message = "AP open item was not found in the active company context." })
                    : Results.Ok(attempt);
            });

        accounting.MapGet(
            "/open-items/ap/{openItemId:guid}/adjustment-request",
            async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var request = await openItemRepository.GetLatestAdjustmentRequestAsync(
                    query.CompanyId,
                    openItemId,
                    cancellationToken);

                return request is null
                    ? Results.NotFound(new { message = "No AP open item adjustment request was found for the active company context." })
                    : Results.Ok(request);
            });

        accounting.MapPost(
            "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/submit",
            async (Guid openItemId, Guid requestId, TransitionOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var result = await openItemRepository.SubmitAdjustmentRequestAsync(
                    request.CompanyId,
                    openItemId,
                    requestId,
                    actorId,
                    cancellationToken);

                return result is null
                    ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
                    : Results.Ok(result);
            });

        accounting.MapPost(
            "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/cancel",
            async (Guid openItemId, Guid requestId, TransitionOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var result = await openItemRepository.CancelAdjustmentRequestAsync(
                    request.CompanyId,
                    openItemId,
                    requestId,
                    actorId,
                    cancellationToken);

                return result is null
                    ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
                    : Results.Ok(result);
            });

        accounting.MapPost(
            "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/approve",
            async (Guid openItemId, Guid requestId, GovernOpenItemAdjustmentApprovalHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var companyId = CompanyId.Parse(request.CompanyId.ToString());
                var currentRequest = await openItemRepository.GetLatestAdjustmentRequestAsync(
                    companyId,
                    openItemId,
                    cancellationToken);

                if (currentRequest is null || currentRequest.RequestId != requestId)
                {
                    return Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." });
                }

                var authorityBlock = RequireOpenItemAdjustmentApprovalAuthority(
                    sessionAccessor.Current,
                    currentRequest,
                    "AP",
                    "approve");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var result = await openItemRepository.ApproveAdjustmentRequestAsync(
                    companyId,
                    openItemId,
                    requestId,
                    actorId,
                    cancellationToken);

                return result is null
                    ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
                    : Results.Ok(result);
            });

        accounting.MapPost(
            "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/reject",
            async (Guid openItemId, Guid requestId, GovernOpenItemAdjustmentApprovalHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var companyId = CompanyId.Parse(request.CompanyId.ToString());
                var currentRequest = await openItemRepository.GetLatestAdjustmentRequestAsync(
                    companyId,
                    openItemId,
                    cancellationToken);

                if (currentRequest is null || currentRequest.RequestId != requestId)
                {
                    return Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." });
                }

                var authorityBlock = RequireOpenItemAdjustmentApprovalAuthority(
                    sessionAccessor.Current,
                    currentRequest,
                    "AP",
                    "reject");
                if (authorityBlock is not null)
                {
                    return authorityBlock;
                }

                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                var result = await openItemRepository.RejectAdjustmentRequestAsync(
                    companyId,
                    openItemId,
                    requestId,
                    actorId,
                    cancellationToken);

                return result is null
                    ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
                    : Results.Ok(result);
            });

        accounting.MapGet(
            "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/readiness",
            async (Guid openItemId, Guid requestId, [AsParameters] OpenItemAdjustmentRequestReadinessQuery query, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var readiness = await openItemRepository.GetAdjustmentRequestReadinessAsync(
                    query.CompanyId,
                    openItemId,
                    requestId,
                    asOfDate,
                    cancellationToken);

                return readiness is null
                    ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
                    : Results.Ok(readiness);
            });

        accounting.MapGet(
            "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/execution-plan",
            async (Guid openItemId, Guid requestId, [AsParameters] OpenItemAdjustmentRequestReadinessQuery query, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var plan = await openItemRepository.GetAdjustmentRequestExecutionPlanAsync(
                    query.CompanyId,
                    openItemId,
                    requestId,
                    asOfDate,
                    cancellationToken);

                return plan is null
                    ? Results.NotFound(new { message = "AP open item adjustment request was not found in the active company context." })
                    : Results.Ok(plan);
            });

        accounting.MapPost(
            "/open-items/ap/{openItemId:guid}/adjustment-request/{requestId:guid}/execute",
            async (Guid openItemId, Guid requestId, ExecuteOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, PostApOpenItemAdjustmentCommandHandler handler, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var actorId = request.UserId ?? sessionAccessor.Current?.UserId;
                if (!actorId.HasValue)
                {
                    return Results.BadRequest(new { message = "A user id is required to execute a governed AP open item adjustment." });
                }

                try
                {
                    var result = await handler.HandleAsync(
                        new PostApOpenItemAdjustmentCommand(
                            request.CompanyId,
                            openItemId,
                            requestId,
                            actorId.Value,
                            request.AdjustmentAccountId,
                            request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });
    }
}
