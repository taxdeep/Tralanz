using Citus.Accounting.Api;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Queries;
using Citus.Accounting.Application.Repositories;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Journal;
using Citus.Ui.Shared.Reports;
using Citus.Ui.Shared.Shell;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Infrastructure.PostgreSQL.Company;
using Modules.Company.MultiBook;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("AccountingCore") ??
    builder.Configuration["CITUS_ACCOUNTING_DB"];

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "A PostgreSQL connection string is required. Configure ConnectionStrings:AccountingCore or CITUS_ACCOUNTING_DB.");
}

builder.Services.AddSingleton(new PostgresConnectionFactory(connectionString));
builder.Services.AddSingleton<PostgresExecutionContextAccessor>();
builder.Services.AddSingleton(new PlatformPostgresConnectionFactory(connectionString));
builder.Services.Configure<BusinessSessionOptions>(builder.Configuration.GetSection(BusinessSessionOptions.SectionName));
builder.Services.AddSingleton<IPlatformRuntimeStateRepository, PostgresPlatformRuntimeStateRepository>();
builder.Services.AddSingleton<BusinessSessionDirectory>();
builder.Services.AddScoped<BusinessSessionContextAccessor>();
builder.Services.AddSingleton<BusinessSessionRequestReader>();
builder.Services.AddSingleton<BusinessRequestContractGuard>();
builder.Services.AddSingleton<BusinessRouteGuard>();
builder.Services.AddScoped<IManualJournalDocumentRepository, PostgresManualJournalDocumentRepository>();
builder.Services.AddScoped<IInvoiceDocumentRepository, PostgresInvoiceDocumentRepository>();
builder.Services.AddScoped<ICreditNoteDocumentRepository, PostgresCreditNoteDocumentRepository>();
builder.Services.AddScoped<IBillDocumentRepository, PostgresBillDocumentRepository>();
builder.Services.AddScoped<IVendorCreditDocumentRepository, PostgresVendorCreditDocumentRepository>();
builder.Services.AddScoped<IReceivePaymentDocumentRepository, PostgresReceivePaymentDocumentRepository>();
builder.Services.AddScoped<ICreditApplicationDocumentRepository, PostgresCreditApplicationDocumentRepository>();
builder.Services.AddScoped<IPayBillDocumentRepository, PostgresPayBillDocumentRepository>();
builder.Services.AddScoped<IVendorCreditApplicationDocumentRepository, PostgresVendorCreditApplicationDocumentRepository>();
builder.Services.AddScoped<IFxRevaluationDocumentRepository, PostgresFxRevaluationDocumentRepository>();
builder.Services.AddScoped<IAccountingReportRepository, PostgresAccountingReportRepository>();
builder.Services.AddScoped<IAccountingDocumentReviewRepository, PostgresAccountingDocumentReviewRepository>();
builder.Services.AddScoped<IJournalEntryReviewRepository, PostgresJournalEntryReviewRepository>();
builder.Services.AddScoped<IFxSnapshotRepository, PostgresFxSnapshotRepository>();
builder.Services.AddScoped<ICompanyBookPolicyStore, PostgreSqlCompanyBookPolicyStore>();
builder.Services.AddScoped<ICompanyBookPolicyWorkflow, CompanyBookPolicyWorkflow>();
builder.Services.AddScoped<IArOpenItemRepository, PostgresArOpenItemRepository>();
builder.Services.AddScoped<IApOpenItemRepository, PostgresApOpenItemRepository>();
builder.Services.AddScoped<ISettlementApplicationRepository, PostgresSettlementApplicationRepository>();
builder.Services.AddScoped<IFxRevaluationApplyRepository, PostgresFxRevaluationApplyRepository>();
builder.Services.AddScoped<IUnitOfWork, PostgresUnitOfWork>();
builder.Services.AddScoped<IPostingValidator, DefaultPostingValidator>();
builder.Services.AddScoped<ITaxEngine, NullTaxEngine>();
builder.Services.AddScoped<IFxResolutionService, LocalFirstFxResolutionService>();
builder.Services.AddScoped<IPostingFragmentBuilder, AccountingPostingFragmentBuilder>();
builder.Services.AddScoped<IJournalAggregator, DefaultJournalAggregator>();
builder.Services.AddScoped<IJournalEntryWriter, PostgresJournalEntryWriter>();
builder.Services.AddScoped<IPostingEngine, DefaultPostingEngine>();
builder.Services.AddScoped<PostManualJournalCommandHandler>();
builder.Services.AddScoped<PostInvoiceCommandHandler>();
builder.Services.AddScoped<PostCreditNoteCommandHandler>();
builder.Services.AddScoped<PostBillCommandHandler>();
builder.Services.AddScoped<PostVendorCreditCommandHandler>();
builder.Services.AddScoped<PrepareReceivePaymentDraftCommandHandler>();
builder.Services.AddScoped<PostReceivePaymentCommandHandler>();
builder.Services.AddScoped<PostCreditApplicationCommandHandler>();
builder.Services.AddScoped<PreparePayBillDraftCommandHandler>();
builder.Services.AddScoped<PostPayBillCommandHandler>();
builder.Services.AddScoped<PostVendorCreditApplicationCommandHandler>();
builder.Services.AddScoped<PrepareFxRevaluationBatchCommandHandler>();
builder.Services.AddScoped<PrepareFxRevaluationUnwindBatchCommandHandler>();
builder.Services.AddScoped<PrepareFxRevaluationCascadeUnwindBatchCommandHandler>();
builder.Services.AddScoped<PostFxRevaluationBatchCommandHandler>();
builder.Services.AddScoped<PostFxRevaluationCascadeUnwindCommandHandler>();

var app = builder.Build();

await using (var startupScope = app.Services.CreateAsyncScope())
{
    var runtimeStateRepository = startupScope.ServiceProvider.GetRequiredService<IPlatformRuntimeStateRepository>();
    await runtimeStateRepository.EnsureSchemaAsync(CancellationToken.None);
}

var accounting = app.MapGroup("/accounting");

accounting.AddEndpointFilterFactory(
    (factoryContext, next) =>
    {
        return async invocationContext =>
        {
            var services = invocationContext.HttpContext.RequestServices;
            var runtimeStateRepository = services.GetRequiredService<IPlatformRuntimeStateRepository>();
            var routeGuard = services.GetRequiredService<BusinessRouteGuard>();
            var sessionAccessor = services.GetRequiredService<BusinessSessionContextAccessor>();
            var maintenanceState = await runtimeStateRepository.GetMaintenanceStateAsync(invocationContext.HttpContext.RequestAborted);
            var guardResult = routeGuard.Evaluate(
                invocationContext.HttpContext.Request.Method,
                invocationContext.HttpContext.Request.Headers,
                invocationContext.Arguments as IReadOnlyList<object?> ?? invocationContext.Arguments.ToArray(),
                maintenanceState);

            if (!guardResult.Allowed)
            {
                return Results.Json(
                    new
                    {
                        message = guardResult.Message,
                        maintenanceEnabled = maintenanceState?.Enabled ?? false,
                        maintenanceMessage = maintenanceState?.Message,
                        scheduledUntilUtc = maintenanceState?.ScheduledUntilUtc,
                        requiredHeaders = new[]
                        {
                            BusinessSessionHeaders.UserId,
                            BusinessSessionHeaders.ActiveCompanyId
                        }
                    },
                    statusCode: guardResult.StatusCode);
            }

            if (guardResult.Session is not null)
            {
                sessionAccessor.Set(guardResult.Session);
            }

            return await next(invocationContext);
        };
    });

app.MapGet("/", () => Results.Ok(new
{
    service = "Citus.Accounting.Api",
    status = "registered-through-platform-core",
    authority = "CITUS_PRODUCT_ENGINEERING_AUTHORITY.md",
    storage = "PostgreSQL",
    module = "accounting",
    core = "Citus.Platform.Core"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "Citus.Accounting.Api",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/architecture", () => Results.Ok(new
{
    layers = new[]
    {
        "PlatformCore",
        "Domain",
        "Application",
        "Infrastructure",
        "Api"
    },
    postingRule = "All formal accounting must go through the Posting Engine.",
    moduleRegistration = "accounting module is governed by Citus.Platform.Core metadata"
}));

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
            return Results.BadRequest(new
            {
                message = ex.Message
            });
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
            return Results.BadRequest(new
            {
                message = ex.Message
            });
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
            return Results.BadRequest(new
            {
                message = ex.Message
            });
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
            return Results.BadRequest(new
            {
                message = ex.Message
            });
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
        if (!sessionDirectory.TryResolve(session, out var resolution, out var error) || resolution is null)
        {
            return Results.Json(
                new
                {
                    message = error ?? "Business session context could not be resolved for the current environment."
                },
                statusCode: StatusCodes.Status403Forbidden);
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

accounting.MapGet(
    "/reports/trial-balance",
    async ([AsParameters] TrialBalanceLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetTrialBalanceAsync(
            new GetTrialBalanceQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapTrialBalanceReport(report));
    });

accounting.MapGet(
    "/reports/trial-balance/export.csv",
    async ([AsParameters] TrialBalanceLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetTrialBalanceAsync(
            new GetTrialBalanceQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportTrialBalance(MapTrialBalanceReport(report));
        return ToCsvFileResult(file);
    });

accounting.MapGet(
    "/reports/income-statement",
    async ([AsParameters] IncomeStatementLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var dateTo = query.DateTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var dateFrom = query.DateFrom ?? new DateOnly(dateTo.Year, dateTo.Month, 1);

        if (dateFrom > dateTo)
        {
            return Results.BadRequest(new
            {
                message = "Income Statement date range is invalid. dateFrom must be on or before dateTo."
            });
        }

        var report = await repository.GetIncomeStatementAsync(
            new GetIncomeStatementQuery(
                new(query.CompanyId),
                dateFrom,
                dateTo,
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapIncomeStatementReport(report));
    });

accounting.MapGet(
    "/reports/income-statement/export.csv",
    async ([AsParameters] IncomeStatementLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var dateTo = query.DateTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var dateFrom = query.DateFrom ?? new DateOnly(dateTo.Year, dateTo.Month, 1);

        if (dateFrom > dateTo)
        {
            return Results.BadRequest(new
            {
                message = "Income Statement date range is invalid. dateFrom must be on or before dateTo."
            });
        }

        var report = await repository.GetIncomeStatementAsync(
            new GetIncomeStatementQuery(
                new(query.CompanyId),
                dateFrom,
                dateTo,
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportIncomeStatement(MapIncomeStatementReport(report));
        return ToCsvFileResult(file);
    });

accounting.MapGet(
    "/reports/balance-sheet",
    async ([AsParameters] BalanceSheetLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetBalanceSheetAsync(
            new GetBalanceSheetQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapBalanceSheetReport(report));
    });

accounting.MapGet(
    "/reports/balance-sheet/export.csv",
    async ([AsParameters] BalanceSheetLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetBalanceSheetAsync(
            new GetBalanceSheetQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                query.IncludeZeroBalances),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportBalanceSheet(MapBalanceSheetReport(report));
        return ToCsvFileResult(file);
    });

accounting.MapGet(
    "/reports/ar-aging",
    async ([AsParameters] ArAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetArAgingAsync(
            new GetArAgingQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapArAgingReport(report));
    });

accounting.MapGet(
    "/reports/ar-aging/export.csv",
    async ([AsParameters] ArAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetArAgingAsync(
            new GetArAgingQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportArAging(MapArAgingReport(report));
        return ToCsvFileResult(file);
    });

accounting.MapGet(
    "/reports/ap-aging",
    async ([AsParameters] ApAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetApAgingAsync(
            new GetApAgingQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        return Results.Ok(MapApAgingReport(report));
    });

accounting.MapGet(
    "/reports/ap-aging/export.csv",
    async ([AsParameters] ApAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
    {
        var report = await repository.GetApAgingAsync(
            new GetApAgingQuery(
                new(query.CompanyId),
                query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
            cancellationToken);

        if (report is null)
        {
            return Results.NotFound(new
            {
                message = "The active company is not provisioned in the accounting core yet."
            });
        }

        var file = ReportCsvExporter.ExportApAging(MapApAgingReport(report));
        return ToCsvFileResult(file);
    });

accounting.MapGet(
    "/open-items/ar/{openItemId:guid}",
    async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IArOpenItemRepository openItemRepository, ISettlementApplicationRepository settlementRepository, CancellationToken cancellationToken) =>
    {
        var item = await openItemRepository.GetDrillDownAsync(
            new(query.CompanyId),
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
            new(query.CompanyId),
            "ar_open_item",
            openItemId,
            cancellationToken);

        return Results.Ok(new
        {
            OpenItem = new
            {
                item.OpenItemId,
                item.OpenItemType,
                CompanyId = item.CompanyId.Value,
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
    "/open-items/ap/{openItemId:guid}",
    async (Guid openItemId, [AsParameters] OpenItemDrillDownLookupQuery query, IApOpenItemRepository openItemRepository, ISettlementApplicationRepository settlementRepository, CancellationToken cancellationToken) =>
    {
        var item = await openItemRepository.GetDrillDownAsync(
            new(query.CompanyId),
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
            new(query.CompanyId),
            "ap_open_item",
            openItemId,
            cancellationToken);

        return Results.Ok(new
        {
            OpenItem = new
            {
                item.OpenItemId,
                item.OpenItemType,
                CompanyId = item.CompanyId.Value,
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
    "/document-review/{sourceType}/{documentId:guid}",
    async (
        string sourceType,
        Guid documentId,
        [AsParameters] DocumentReviewLookupQuery query,
        IAccountingDocumentReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var review = await repository.GetSourceDocumentAsync(
            new(query.CompanyId),
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
            CompanyId = review.CompanyId.Value,
            review.EntityNumber,
            review.DisplayNumber,
            review.Status,
            review.DocumentDate,
            review.DueDate,
            CounterpartyLabel = MapDocumentReviewCounterpartyLabel(review.CounterpartyRole),
            review.CounterpartyId,
            ControlAccountLabel = MapDocumentReviewControlAccountLabel(review.CounterpartyRole),
            review.ControlAccountId,
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

accounting.MapGet(
    "/journal-entries",
    async (
        [AsParameters] JournalEntryListLookupQuery query,
        IJournalEntryReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var items = await repository.ListRecentAsync(
            new(query.CompanyId),
            query.Take,
            cancellationToken);

        return Results.Ok(items.Select(MapJournalEntryReviewListItem).ToArray());
    });

accounting.MapGet(
    "/journal-entries/by-source/{sourceType}/{sourceId:guid}",
    async (
        string sourceType,
        Guid sourceId,
        [AsParameters] JournalEntryLookupQuery query,
        IJournalEntryReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var item = await repository.FindBySourceAsync(
            new(query.CompanyId),
            sourceType,
            sourceId,
            cancellationToken);

        if (item is null)
        {
            return Results.NotFound(new
            {
                message = "Journal entry was not found for the requested source document in the active company context."
            });
        }

        return Results.Ok(MapJournalEntryReviewListItem(item));
    });

accounting.MapGet(
    "/journal-entries/{journalEntryId:guid}",
    async (
        Guid journalEntryId,
        [AsParameters] JournalEntryLookupQuery query,
        IJournalEntryReviewRepository repository,
        CancellationToken cancellationToken) =>
    {
        var review = await repository.GetAsync(
            new(query.CompanyId),
            journalEntryId,
            cancellationToken);

        if (review is null)
        {
            return Results.NotFound(new
            {
                message = "Journal entry was not found in the active company context."
            });
        }

        return Results.Ok(MapJournalEntryReview(review));
    });

accounting.MapPost(
    "/fx-revaluation-batches/prepare",
    async (PrepareFxRevaluationBatchHttpRequest request, PrepareFxRevaluationBatchCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PrepareFxRevaluationBatchCommand(
                    new(request.CompanyId),
                    new(request.UserId),
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
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
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
                new(query.CompanyId),
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
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
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
    async (Guid documentId, PrepareFxRevaluationUnwindBatchHttpRequest request, PostFxRevaluationCascadeUnwindCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostFxRevaluationCascadeUnwindCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.UnwindDate,
                    request.Memo,
                    request.IdempotencyKey),
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
    "/fx-revaluation-batches/{documentId:guid}",
    async (Guid documentId, [AsParameters] FxRevaluationBatchLookupQuery query, IFxRevaluationDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
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
            CompanyId = document.CompanyId.Value,
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
    });

accounting.MapPost(
    "/fx-revaluation-batches/{documentId:guid}/post",
    async (Guid documentId, PostFxRevaluationBatchHttpRequest request, PostFxRevaluationBatchCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostFxRevaluationBatchCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
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
    "/manual-journals/{documentId:guid}",
    async (Guid documentId, [AsParameters] ManualJournalLookupQuery query, IManualJournalDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
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
            CompanyId = document.CompanyId.Value,
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
    async (Guid documentId, PostManualJournalHttpRequest request, PostManualJournalCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostManualJournalCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
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
    "/invoices/{documentId:guid}",
    async (Guid documentId, [AsParameters] InvoiceLookupQuery query, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
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
            CompanyId = document.CompanyId.Value,
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
            Lines = document.InvoiceLines.Select(line => new
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
    "/invoices/{documentId:guid}/post",
    async (Guid documentId, PostInvoiceHttpRequest request, PostInvoiceCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostInvoiceCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
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
    "/credit-notes/{documentId:guid}",
    async (Guid documentId, [AsParameters] CreditNoteLookupQuery query, ICreditNoteDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
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
            CompanyId = document.CompanyId.Value,
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
    async (Guid documentId, PostCreditNoteHttpRequest request, PostCreditNoteCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostCreditNoteCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
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
    "/bills/{documentId:guid}",
    async (Guid documentId, [AsParameters] BillLookupQuery query, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Bill document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
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
            Lines = document.BillLines.Select(line => new
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
    "/bills/{documentId:guid}/post",
    async (Guid documentId, PostBillHttpRequest request, PostBillCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostBillCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
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
    "/vendor-credits/{documentId:guid}",
    async (Guid documentId, [AsParameters] VendorCreditLookupQuery query, IVendorCreditDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
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
            CompanyId = document.CompanyId.Value,
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
    async (Guid documentId, PostVendorCreditHttpRequest request, PostVendorCreditCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostVendorCreditCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
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
    "/receive-payments/{documentId:guid}",
    async (Guid documentId, [AsParameters] ReceivePaymentLookupQuery query, IReceivePaymentDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Receive payment document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            CustomerId = document.PartyId,
            document.BankAccountId,
            document.ReceivableAccountId,
            document.RealizedFxGainAccountId,
            document.RealizedFxLossAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.TotalAmount,
            document.Memo,
            Lines = document.PaymentLines.Select(line => new
            {
                line.LineNumber,
                line.TargetArOpenItemId,
                line.Description,
                line.AppliedAmount,
                line.AppliedAmountBase,
                line.CarryingAmountBase
            })
        });
    });

accounting.MapPost(
    "/receive-payments/prepare",
    async (PrepareReceivePaymentDraftHttpRequest request, PrepareReceivePaymentDraftCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PrepareReceivePaymentDraftCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    request.CustomerId,
                    request.BankAccountId,
                    request.PaymentDate,
                    request.AcceptedFxSnapshotId,
                    request.Memo,
                    request.Lines.Select(line => new SettlementDraftLine(line.TargetOpenItemId, line.AppliedAmountTx)).ToArray()),
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
    "/customers/{customerId:guid}/open-receivables",
    async (Guid customerId, [AsParameters] OpenReceivablesLookupQuery query, IReceivePaymentDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var candidates = await repository.ListOpenReceivableCandidatesAsync(
            new(query.CompanyId),
            customerId,
            cancellationToken);

        return Results.Ok(candidates.Select(candidate => new
        {
            candidate.OpenItemId,
            candidate.SourceType,
            candidate.SourceDocumentId,
            candidate.DisplayNumber,
            candidate.DocumentDate,
            candidate.DueDate,
            candidate.DocumentCurrencyCode,
            candidate.BaseCurrencyCode,
            candidate.OriginalAmountTx,
            candidate.OpenAmountTx,
            candidate.OpenAmountBase,
            candidate.BalanceSide,
            candidate.Status
        }));
    });

accounting.MapPost(
    "/receive-payments/{documentId:guid}/post",
    async (Guid documentId, PostReceivePaymentHttpRequest request, PostReceivePaymentCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostReceivePaymentCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
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
    "/credit-applications/{documentId:guid}",
    async (Guid documentId, [AsParameters] CreditApplicationLookupQuery query, ICreditApplicationDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Credit application document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            CustomerId = document.PartyId,
            ReceivableAccountId = document.ReceivableAccountId,
            document.RealizedFxGainAccountId,
            document.RealizedFxLossAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.TotalAmount,
            document.Memo,
            Lines = document.ApplicationLines.Select(line => new
            {
                line.LineNumber,
                line.SourceCreditArOpenItemId,
                line.TargetInvoiceArOpenItemId,
                line.Description,
                line.AppliedAmount,
                line.SourceCarryingAmountBase,
                line.TargetCarryingAmountBase,
                line.RealizedFxAmountBase
            })
        });
    });

accounting.MapPost(
    "/credit-applications/{documentId:guid}/post",
    async (Guid documentId, PostCreditApplicationHttpRequest request, PostCreditApplicationCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostCreditApplicationCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.IdempotencyKey),
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
    "/pay-bills/prepare",
    async (PreparePayBillDraftHttpRequest request, PreparePayBillDraftCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PreparePayBillDraftCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    request.VendorId,
                    request.BankAccountId,
                    request.PaymentDate,
                    request.AcceptedFxSnapshotId,
                    request.Memo,
                    request.Lines.Select(line => new SettlementDraftLine(line.TargetOpenItemId, line.AppliedAmountTx)).ToArray()),
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
    "/vendors/{vendorId:guid}/open-payables",
    async (Guid vendorId, [AsParameters] OpenPayablesLookupQuery query, IPayBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var candidates = await repository.ListOpenPayableCandidatesAsync(
            new(query.CompanyId),
            vendorId,
            cancellationToken);

        return Results.Ok(candidates.Select(candidate => new
        {
            candidate.OpenItemId,
            candidate.SourceType,
            candidate.SourceDocumentId,
            candidate.DisplayNumber,
            candidate.DocumentDate,
            candidate.DueDate,
            candidate.DocumentCurrencyCode,
            candidate.BaseCurrencyCode,
            candidate.OriginalAmountTx,
            candidate.OpenAmountTx,
            candidate.OpenAmountBase,
            candidate.BalanceSide,
            candidate.Status
        }));
    });

accounting.MapGet(
    "/pay-bills/{documentId:guid}",
    async (Guid documentId, [AsParameters] PayBillLookupQuery query, IPayBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Pay bill document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            VendorId = document.PartyId,
            document.BankAccountId,
            document.PayableAccountId,
            document.RealizedFxGainAccountId,
            document.RealizedFxLossAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.TotalAmount,
            document.Memo,
            Lines = document.PaymentLines.Select(line => new
            {
                line.LineNumber,
                line.TargetApOpenItemId,
                line.Description,
                line.AppliedAmount,
                line.AppliedAmountBase,
                line.CarryingAmountBase
            })
        });
    });

accounting.MapPost(
    "/pay-bills/{documentId:guid}/post",
    async (Guid documentId, PostPayBillHttpRequest request, PostPayBillCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostPayBillCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.AcceptedFxSnapshotId,
                    request.IdempotencyKey),
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
    "/vendor-credit-applications/{documentId:guid}",
    async (Guid documentId, [AsParameters] VendorCreditApplicationLookupQuery query, IVendorCreditApplicationDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        if (document is null)
        {
            return Results.NotFound(new
            {
                message = "Vendor credit application document was not found in the active company context."
            });
        }

        return Results.Ok(new
        {
            document.Id,
            CompanyId = document.CompanyId.Value,
            EntityNumber = document.EntityNumber.Value,
            DisplayNumber = document.DisplayNumber.Value,
            document.Status,
            document.DocumentDate,
            VendorId = document.PartyId,
            PayableAccountId = document.PayableAccountId,
            document.RealizedFxGainAccountId,
            document.RealizedFxLossAccountId,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            document.TotalAmount,
            document.Memo,
            Lines = document.ApplicationLines.Select(line => new
            {
                line.LineNumber,
                line.SourceVendorCreditApOpenItemId,
                line.TargetBillApOpenItemId,
                line.Description,
                line.AppliedAmount,
                line.SourceCarryingAmountBase,
                line.TargetCarryingAmountBase,
                line.RealizedFxAmountBase
            })
        });
    });

accounting.MapPost(
    "/vendor-credit-applications/{documentId:guid}/post",
    async (Guid documentId, PostVendorCreditApplicationHttpRequest request, PostVendorCreditApplicationCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostVendorCreditApplicationCommand(
                    new(request.CompanyId),
                    documentId,
                    new(request.UserId),
                    request.IdempotencyKey),
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

app.Run();

static IResult ToCsvFileResult(ReportCsvExporter.ReportCsvFile file) =>
    Results.File(Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);

static string MapDocumentReviewSourceLabel(string sourceType) =>
    sourceType switch
    {
        "manual_journal" => "Manual Journal",
        "invoice" => "Invoice",
        "credit_note" => "Credit Note",
        "bill" => "Bill",
        "vendor_credit" => "Vendor Credit",
        "receive_payment" => "Receive Payment",
        "credit_application" => "Credit Application",
        "pay_bill" => "Pay Bill",
        "vendor_credit_application" => "Vendor Credit Application",
        _ => "Document"
    };

static string MapDocumentReviewCounterpartyLabel(string counterpartyRole) =>
    counterpartyRole switch
    {
        "journal" => "Journal context",
        "customer" => "Customer",
        "vendor" => "Vendor",
        _ => "Counterparty"
    };

static string MapDocumentReviewControlAccountLabel(string counterpartyRole) =>
    counterpartyRole switch
    {
        "journal" => "Balancing logic",
        "customer" => "Receivable account",
        "vendor" => "Payable account",
        _ => "Control account"
    };

static string MapDocumentReviewLineAccountLabel(string counterpartyRole) =>
    counterpartyRole switch
    {
        "journal" => "Journal account",
        "customer" => "Revenue account",
        "vendor" => "Expense account",
        _ => "Account"
    };

static JournalEntryReviewListItemSummary MapJournalEntryReviewListItem(JournalEntryReviewListItem item) =>
    new()
    {
        Id = item.Id,
        CompanyId = item.CompanyId.Value,
        EntityNumber = item.EntityNumber,
        DisplayNumber = item.DisplayNumber,
        Status = item.Status,
        SourceType = item.SourceType,
        SourceTypeLabel = MapJournalEntrySourceTypeLabel(item.SourceType),
        SourceId = item.SourceId,
        TransactionCurrencyCode = item.TransactionCurrencyCode,
        BaseCurrencyCode = item.BaseCurrencyCode,
        TotalTxDebit = item.TotalTxDebit,
        TotalTxCredit = item.TotalTxCredit,
        TotalDebit = item.TotalDebit,
        TotalCredit = item.TotalCredit,
        LineCount = item.LineCount,
        PostedAt = item.PostedAt,
        VoidedAt = item.VoidedAt,
        ReversedAt = item.ReversedAt
    };

static JournalEntryReviewSummary MapJournalEntryReview(JournalEntryReview review) =>
    new()
    {
        Id = review.Id,
        CompanyId = review.CompanyId.Value,
        EntityNumber = review.EntityNumber,
        DisplayNumber = review.DisplayNumber,
        Status = review.Status,
        SourceType = review.SourceType,
        SourceTypeLabel = MapJournalEntrySourceTypeLabel(review.SourceType),
        SourceId = review.SourceId,
        TransactionCurrencyCode = review.TransactionCurrencyCode,
        BaseCurrencyCode = review.BaseCurrencyCode,
        ExchangeRate = review.ExchangeRate,
        ExchangeRateDate = review.ExchangeRateDate,
        ExchangeRateSource = review.ExchangeRateSource,
        FxRateSnapshotId = review.FxRateSnapshotId,
        TotalTxDebit = review.TotalTxDebit,
        TotalTxCredit = review.TotalTxCredit,
        TotalDebit = review.TotalDebit,
        TotalCredit = review.TotalCredit,
        LineCount = review.LineCount,
        PostedAt = review.PostedAt,
        VoidedAt = review.VoidedAt,
        ReversedAt = review.ReversedAt,
        CreatedByUserId = review.CreatedByUserId,
        Lines = review.Lines.Select(MapJournalEntryReviewLine).ToArray()
    };

static JournalEntryReviewLineSummary MapJournalEntryReviewLine(JournalEntryReviewLine line) =>
    new()
    {
        LineId = line.LineId,
        LineNumber = line.LineNumber,
        AccountId = line.AccountId,
        AccountCode = line.AccountCode,
        AccountName = line.AccountName,
        RootType = line.RootType,
        DetailType = line.DetailType,
        Description = line.Description,
        TxDebit = line.TxDebit,
        TxCredit = line.TxCredit,
        Debit = line.Debit,
        Credit = line.Credit,
        TaxComponentType = line.TaxComponentType,
        ControlRole = line.ControlRole,
        PartyId = line.PartyId
    };

static string MapJournalEntrySourceTypeLabel(string sourceType) =>
    sourceType switch
    {
        "manual_journal" => "Manual Journal",
        "invoice" => "Invoice",
        "credit_note" => "Credit Note",
        "bill" => "Bill",
        "vendor_credit" => "Vendor Credit",
        "receive_payment" => "Receive Payment",
        "credit_application" => "Credit Application",
        "pay_bill" => "Pay Bill",
        "vendor_credit_application" => "Vendor Credit Application",
        "fx_revaluation" => "FX Revaluation",
        _ => "Source Document"
    };

static TrialBalanceReportSummary MapTrialBalanceReport(TrialBalanceReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        IncludeZeroBalanceAccounts = report.IncludeZeroBalanceAccounts,
        AccountCount = report.AccountCount,
        TotalBalanceDebit = report.TotalBalanceDebit,
        TotalBalanceCredit = report.TotalBalanceCredit,
        IsBalanced = report.IsBalanced,
        Rows = report.Rows
            .Select(
                static row => new TrialBalanceAccountSummary
                {
                    AccountId = row.AccountId,
                    EntityNumber = row.EntityNumber,
                    Code = row.Code,
                    Name = row.Name,
                    RootType = row.RootType,
                    DetailType = row.DetailType,
                    IsActive = row.IsActive,
                    IsSystem = row.IsSystem,
                    PostedDebitTotal = row.PostedDebitTotal,
                    PostedCreditTotal = row.PostedCreditTotal,
                    BalanceDebit = row.BalanceDebit,
                    BalanceCredit = row.BalanceCredit,
                    NetBalance = row.NetBalance,
                    BalanceSide = row.BalanceSide
                })
            .ToArray()
    };

static IncomeStatementReportSummary MapIncomeStatementReport(IncomeStatementReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        DateFrom = report.DateFrom,
        DateTo = report.DateTo,
        BaseCurrencyCode = report.BaseCurrencyCode,
        IncludeZeroBalanceAccounts = report.IncludeZeroBalanceAccounts,
        AccountCount = report.AccountCount,
        TotalRevenue = report.TotalRevenue,
        TotalCostOfSales = report.TotalCostOfSales,
        GrossProfit = report.GrossProfit,
        TotalExpenses = report.TotalExpenses,
        NetIncome = report.NetIncome,
        RevenueRows = report.RevenueRows.Select(MapIncomeStatementRow).ToArray(),
        CostOfSalesRows = report.CostOfSalesRows.Select(MapIncomeStatementRow).ToArray(),
        ExpenseRows = report.ExpenseRows.Select(MapIncomeStatementRow).ToArray()
    };

static BalanceSheetReportSummary MapBalanceSheetReport(BalanceSheetReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        IncludeZeroBalanceAccounts = report.IncludeZeroBalanceAccounts,
        AccountCount = report.AccountCount,
        TotalAssets = report.TotalAssets,
        TotalLiabilities = report.TotalLiabilities,
        CurrentEarnings = report.CurrentEarnings,
        TotalEquity = report.TotalEquity,
        TotalLiabilitiesAndEquity = report.TotalLiabilitiesAndEquity,
        IsBalanced = report.IsBalanced,
        AssetRows = report.AssetRows.Select(MapBalanceSheetRow).ToArray(),
        LiabilityRows = report.LiabilityRows.Select(MapBalanceSheetRow).ToArray(),
        EquityRows = report.EquityRows.Select(MapBalanceSheetRow).ToArray()
    };

static ArAgingReportSummary MapArAgingReport(ArAgingReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        CustomerCount = report.CustomerCount,
        OpenItemCount = report.OpenItemCount,
        CurrentAmountBase = report.CurrentAmountBase,
        Days1To30AmountBase = report.Days1To30AmountBase,
        Days31To60AmountBase = report.Days31To60AmountBase,
        Days61To90AmountBase = report.Days61To90AmountBase,
        DaysOver90AmountBase = report.DaysOver90AmountBase,
        TotalOverdueAmountBase = report.TotalOverdueAmountBase,
        TotalOutstandingAmountBase = report.TotalOutstandingAmountBase,
        CustomerRows = report.CustomerRows.Select(MapArAgingCustomer).ToArray(),
        DetailRows = report.DetailRows.Select(MapArAgingRow).ToArray()
    };

static ApAgingReportSummary MapApAgingReport(ApAgingReport report) =>
    new()
    {
        CompanyId = report.CompanyId,
        AsOfDate = report.AsOfDate,
        BaseCurrencyCode = report.BaseCurrencyCode,
        VendorCount = report.VendorCount,
        OpenItemCount = report.OpenItemCount,
        CurrentAmountBase = report.CurrentAmountBase,
        Days1To30AmountBase = report.Days1To30AmountBase,
        Days31To60AmountBase = report.Days31To60AmountBase,
        Days61To90AmountBase = report.Days61To90AmountBase,
        DaysOver90AmountBase = report.DaysOver90AmountBase,
        TotalOverdueAmountBase = report.TotalOverdueAmountBase,
        TotalOutstandingAmountBase = report.TotalOutstandingAmountBase,
        VendorRows = report.VendorRows.Select(MapApAgingVendor).ToArray(),
        DetailRows = report.DetailRows.Select(MapApAgingRow).ToArray()
    };

static IncomeStatementAccountSummary MapIncomeStatementRow(IncomeStatementAccountAmount row) =>
    new()
    {
        AccountId = row.AccountId,
        EntityNumber = row.EntityNumber,
        Code = row.Code,
        Name = row.Name,
        RootType = row.RootType,
        DetailType = row.DetailType,
        IsActive = row.IsActive,
        IsSystem = row.IsSystem,
        PostedDebitTotal = row.PostedDebitTotal,
        PostedCreditTotal = row.PostedCreditTotal,
        DisplayAmount = row.DisplayAmount
    };

static BalanceSheetAccountSummary MapBalanceSheetRow(BalanceSheetAccountAmount row) =>
    new()
    {
        AccountId = row.AccountId,
        EntityNumber = row.EntityNumber,
        Code = row.Code,
        Name = row.Name,
        RootType = row.RootType,
        DetailType = row.DetailType,
        IsActive = row.IsActive,
        IsSystem = row.IsSystem,
        IsSynthetic = row.IsSynthetic,
        PostedDebitTotal = row.PostedDebitTotal,
        PostedCreditTotal = row.PostedCreditTotal,
        DisplayAmount = row.DisplayAmount
    };

static ArAgingCustomerSummary MapArAgingCustomer(ArAgingCustomerBalance row) =>
    new()
    {
        CustomerId = row.CustomerId,
        CustomerEntityNumber = row.CustomerEntityNumber,
        CustomerDisplayName = row.CustomerDisplayName,
        CustomerIsActive = row.CustomerIsActive,
        OpenItemCount = row.OpenItemCount,
        OldestDueDate = row.OldestDueDate,
        CurrentAmountBase = row.CurrentAmountBase,
        Days1To30AmountBase = row.Days1To30AmountBase,
        Days31To60AmountBase = row.Days31To60AmountBase,
        Days61To90AmountBase = row.Days61To90AmountBase,
        DaysOver90AmountBase = row.DaysOver90AmountBase,
        TotalOverdueAmountBase = row.TotalOverdueAmountBase,
        TotalOutstandingAmountBase = row.TotalOutstandingAmountBase,
        OpenItems = row.OpenItems.Select(MapArAgingRow).ToArray()
    };

static ArAgingOpenItemSummary MapArAgingRow(ArAgingOpenItemAmount row) =>
    new()
    {
        OpenItemId = row.OpenItemId,
        CustomerId = row.CustomerId,
        CustomerEntityNumber = row.CustomerEntityNumber,
        CustomerDisplayName = row.CustomerDisplayName,
        CustomerIsActive = row.CustomerIsActive,
        SourceType = row.SourceType,
        SourceDocumentId = row.SourceDocumentId,
        DisplayNumber = row.DisplayNumber,
        DocumentDate = row.DocumentDate,
        DueDate = row.DueDate,
        DaysPastDue = row.DaysPastDue,
        AgingBucket = row.AgingBucket,
        DocumentCurrencyCode = row.DocumentCurrencyCode,
        BaseCurrencyCode = row.BaseCurrencyCode,
        BalanceSide = row.BalanceSide,
        Status = row.Status,
        OriginalAmountTx = row.OriginalAmountTx,
        OriginalAmountBase = row.OriginalAmountBase,
        OpenAmountTx = row.OpenAmountTx,
        OpenAmountBase = row.OpenAmountBase,
        SignedOpenAmountTx = row.SignedOpenAmountTx,
        SignedOpenAmountBase = row.SignedOpenAmountBase
    };

static ApAgingVendorSummary MapApAgingVendor(ApAgingVendorBalance row) =>
    new()
    {
        VendorId = row.VendorId,
        VendorEntityNumber = row.VendorEntityNumber,
        VendorDisplayName = row.VendorDisplayName,
        VendorIsActive = row.VendorIsActive,
        OpenItemCount = row.OpenItemCount,
        OldestDueDate = row.OldestDueDate,
        CurrentAmountBase = row.CurrentAmountBase,
        Days1To30AmountBase = row.Days1To30AmountBase,
        Days31To60AmountBase = row.Days31To60AmountBase,
        Days61To90AmountBase = row.Days61To90AmountBase,
        DaysOver90AmountBase = row.DaysOver90AmountBase,
        TotalOverdueAmountBase = row.TotalOverdueAmountBase,
        TotalOutstandingAmountBase = row.TotalOutstandingAmountBase,
        OpenItems = row.OpenItems.Select(MapApAgingRow).ToArray()
    };

static ApAgingOpenItemSummary MapApAgingRow(ApAgingOpenItemAmount row) =>
    new()
    {
        OpenItemId = row.OpenItemId,
        VendorId = row.VendorId,
        VendorEntityNumber = row.VendorEntityNumber,
        VendorDisplayName = row.VendorDisplayName,
        VendorIsActive = row.VendorIsActive,
        SourceType = row.SourceType,
        SourceDocumentId = row.SourceDocumentId,
        DisplayNumber = row.DisplayNumber,
        DocumentDate = row.DocumentDate,
        DueDate = row.DueDate,
        DaysPastDue = row.DaysPastDue,
        AgingBucket = row.AgingBucket,
        DocumentCurrencyCode = row.DocumentCurrencyCode,
        BaseCurrencyCode = row.BaseCurrencyCode,
        BalanceSide = row.BalanceSide,
        Status = row.Status,
        OriginalAmountTx = row.OriginalAmountTx,
        OriginalAmountBase = row.OriginalAmountBase,
        OpenAmountTx = row.OpenAmountTx,
        OpenAmountBase = row.OpenAmountBase,
        SignedOpenAmountTx = row.SignedOpenAmountTx,
        SignedOpenAmountBase = row.SignedOpenAmountBase
    };
