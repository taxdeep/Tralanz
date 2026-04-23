using Citus.Accounting.Api;
using Citus.Accounting.Application;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Queries;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Journal;
using Citus.Ui.Shared.Reports;
using Citus.Ui.Shared.Shell;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Citus.Modules.Inventory.Application.Contracts;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Company;
using Infrastructure.PostgreSQL.CompanyAccess;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Inventory;
using Infrastructure.PostgreSQL.Numbering;
using Microsoft.Extensions.Options;
using Modules.CompanyAccess.SessionContext;
using Modules.Company.MultiBook;
using System.Text;
using JournalEntryNumberLookup = Engines.Numbering.JournalEntry.IJournalEntryNumberLookup;
using GlIJournalEntryLifecycleStore = Modules.GL.JournalEntry.IJournalEntryLifecycleStore;
using GlIJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.IJournalEntryLifecycleWorkflow;
using GlJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.JournalEntryLifecycleWorkflow;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration["CITUS_ACCOUNTING_DB"] ??
    builder.Configuration.GetConnectionString("AccountingCore");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "A PostgreSQL connection string is required. Configure ConnectionStrings:AccountingCore or CITUS_ACCOUNTING_DB.");
}

builder.Services.AddSingleton(new PostgresConnectionFactory(connectionString));
builder.Services.AddSingleton(new PostgreSqlConnectionFactory(connectionString));
builder.Services.AddSingleton<PostgresExecutionContextAccessor>();
builder.Services.AddSingleton(new PlatformPostgresConnectionFactory(connectionString));
builder.Services.Configure<BusinessSessionOptions>(builder.Configuration.GetSection(BusinessSessionOptions.SectionName));
builder.Services.AddSingleton<IPlatformRuntimeStateRepository, PostgresPlatformRuntimeStateRepository>();
builder.Services.AddSingleton<ICompanySessionContextStore, PostgreSqlCompanySessionContextStore>();
builder.Services.AddSingleton<ICompanySessionContextWorkflow, CompanySessionContextWorkflow>();
builder.Services.AddSingleton<IInventoryFoundationStore, PostgreSqlInventoryFoundationStore>();
builder.Services.AddSingleton<IInventoryReceiptStore, PostgreSqlInventoryReceiptStore>();
builder.Services.AddSingleton<IReceiptInventoryActivationStore, PostgreSqlReceiptInventoryActivationStore>();
builder.Services.AddSingleton<IReceiptInventoryValuationStore, PostgreSqlReceiptInventoryValuationStore>();
builder.Services.AddSingleton<IReceiptInventoryCostLayerEmissionStore, PostgreSqlReceiptInventoryCostLayerEmissionStore>();
builder.Services.AddSingleton<IReceiptGrIrBridgeStore, PostgreSqlReceiptGrIrBridgeStore>();
builder.Services.AddSingleton<IInventoryIssueStore, PostgreSqlInventoryIssueStore>();
builder.Services.AddSingleton<IInventoryShipmentStore, PostgreSqlInventoryShipmentStore>();
builder.Services.AddSingleton(
    static services => new BusinessSessionDirectory(
        services.GetRequiredService<IOptions<BusinessSessionOptions>>(),
        services.GetService<ICompanySessionContextWorkflow>()));
builder.Services.AddScoped<BusinessSessionContextAccessor>();
builder.Services.AddSingleton<BusinessSessionRequestReader>();
builder.Services.AddSingleton<BusinessRequestContractGuard>();
builder.Services.AddSingleton<BusinessRouteGuard>();
builder.Services.AddScoped<IManualJournalDocumentRepository, PostgresManualJournalDocumentRepository>();
builder.Services.AddScoped<IInvoiceDocumentRepository, PostgresInvoiceDocumentRepository>();
builder.Services.AddScoped<ICreditNoteDocumentRepository, PostgresCreditNoteDocumentRepository>();
builder.Services.AddScoped<IBillDocumentRepository, PostgresBillDocumentRepository>();
builder.Services.AddScoped<IBillReceiptMatchingRepository, PostgresBillReceiptMatchingRepository>();
builder.Services.AddScoped<IReceiptDocumentRepository, PostgresReceiptDocumentRepository>();
builder.Services.AddScoped<IPurchaseOrderDocumentRepository, PostgresPurchaseOrderDocumentRepository>();
builder.Services.AddScoped<IVendorCreditDocumentRepository, PostgresVendorCreditDocumentRepository>();
builder.Services.AddScoped<IReceivePaymentDocumentRepository, PostgresReceivePaymentDocumentRepository>();
builder.Services.AddScoped<ICreditApplicationDocumentRepository, PostgresCreditApplicationDocumentRepository>();
builder.Services.AddScoped<IPayBillDocumentRepository, PostgresPayBillDocumentRepository>();
builder.Services.AddScoped<IVendorCreditApplicationDocumentRepository, PostgresVendorCreditApplicationDocumentRepository>();
builder.Services.AddScoped<IFxRevaluationDocumentRepository, PostgresFxRevaluationDocumentRepository>();
builder.Services.AddScoped<IAccountingReportRepository, PostgresAccountingReportRepository>();
builder.Services.AddScoped<IAccountingDocumentReviewRepository, PostgresAccountingDocumentReviewRepository>();
builder.Services.AddScoped<IJournalEntryReviewRepository, PostgresJournalEntryReviewRepository>();
builder.Services.AddScoped<IReceiptGrIrPostingRepository, PostgresReceiptGrIrPostingRepository>();
builder.Services.AddScoped<IReceiptGrIrClearingAccountPolicyRepository, PostgresReceiptGrIrClearingAccountPolicyRepository>();
builder.Services.AddScoped<IReceiptGrIrApSettlementControlStore, PostgresReceiptGrIrApSettlementControlStore>();
builder.Services.AddScoped<IReceiptGrIrSettlementPostingRepository, PostgresReceiptGrIrSettlementPostingRepository>();
builder.Services.AddSingleton<JournalEntryNumberLookup, PostgreSqlJournalEntryNumberLookup>();
builder.Services.AddSingleton<GlIJournalEntryLifecycleStore, PostgreSqlJournalEntryLifecycleStore>();
builder.Services.AddSingleton<GlIJournalEntryLifecycleWorkflow, GlJournalEntryLifecycleWorkflow>();
builder.Services.AddScoped<IFxSnapshotRepository, PostgresFxSnapshotRepository>();
builder.Services.AddScoped<ICompanyBookPolicyStore, PostgreSqlCompanyBookPolicyStore>();
builder.Services.AddScoped<ICompanyBookPolicyWorkflow, CompanyBookPolicyWorkflow>();
builder.Services.AddScoped<IArOpenItemRepository, PostgresArOpenItemRepository>();
builder.Services.AddScoped<IApOpenItemRepository, PostgresApOpenItemRepository>();
builder.Services.AddScoped<IOpenItemAdjustmentAccountMappingRepository, PostgresOpenItemAdjustmentAccountMappingRepository>();
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
builder.Services.AddScoped<PostReceiptWorkflow>();
builder.Services.AddScoped<PostReceiptGrIrCommandHandler>();
builder.Services.AddScoped<ExecuteReceiptGrIrSettlementCommandHandler>();
builder.Services.AddScoped<PostReceiptGrIrSettlementJournalCommandHandler>();
builder.Services.AddScoped<ClearReceiptGrIrSettlementOpenItemCommandHandler>();
builder.Services.AddScoped<ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler>();
builder.Services.AddScoped<PostVendorCreditCommandHandler>();
builder.Services.AddScoped<PrepareReceivePaymentDraftCommandHandler>();
builder.Services.AddScoped<PostReceivePaymentCommandHandler>();
builder.Services.AddScoped<PostCreditApplicationCommandHandler>();
builder.Services.AddScoped<PreparePayBillDraftCommandHandler>();
builder.Services.AddScoped<PostPayBillCommandHandler>();
builder.Services.AddScoped<PostVendorCreditApplicationCommandHandler>();
builder.Services.AddScoped<PostArOpenItemAdjustmentCommandHandler>();
builder.Services.AddScoped<PostApOpenItemAdjustmentCommandHandler>();
builder.Services.AddScoped<PrepareFxRevaluationBatchCommandHandler>();
builder.Services.AddScoped<PrepareFxRevaluationUnwindBatchCommandHandler>();
builder.Services.AddScoped<PrepareFxRevaluationCascadeUnwindBatchCommandHandler>();
builder.Services.AddScoped<PostFxRevaluationBatchCommandHandler>();
builder.Services.AddScoped<PostFxRevaluationCascadeUnwindCommandHandler>();

var app = builder.Build();

await using (var startupScope = app.Services.CreateAsyncScope())
{
    var runtimeStateRepository = startupScope.ServiceProvider.GetRequiredService<IPlatformRuntimeStateRepository>();
    var adjustmentAccountMappingRepository = startupScope.ServiceProvider.GetRequiredService<IOpenItemAdjustmentAccountMappingRepository>();
    await runtimeStateRepository.EnsureSchemaAsync(CancellationToken.None);
    await adjustmentAccountMappingRepository.EnsureSchemaAsync(CancellationToken.None);
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
            var guardResult = await routeGuard.EvaluateAsync(
                invocationContext.HttpContext.Request.Method,
                invocationContext.HttpContext.Request.Headers,
                invocationContext.Arguments as IReadOnlyList<object?> ?? invocationContext.Arguments.ToArray(),
                maintenanceState,
                invocationContext.HttpContext.RequestAborted);

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
                sessionAccessor.Set(guardResult.Session, guardResult.Resolution);
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
    "/open-item-adjustment-account-mappings",
    async ([AsParameters] OpenItemAdjustmentAccountMappingLookupQuery query, IOpenItemAdjustmentAccountMappingRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.LookupAsync(
                new OpenItemAdjustmentAccountMappingLookupRequest(
                    new(query.CompanyId),
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
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
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
            return Results.BadRequest(new { message = ex.Message });
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
            new(request.CompanyId),
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
            new(query.CompanyId),
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
            new(request.CompanyId),
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
            new(query.CompanyId),
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
            new(request.CompanyId),
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
            new(request.CompanyId),
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
        var companyId = new CompanyId(request.CompanyId);
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
        var companyId = new CompanyId(request.CompanyId);
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
            new(query.CompanyId),
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
            new(query.CompanyId),
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
    async (Guid openItemId, Guid requestId, ExecuteOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, PostArOpenItemAdjustmentCommandHandler handler, CancellationToken cancellationToken) =>
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
                    new(request.CompanyId),
                    openItemId,
                    requestId,
                    new(actorId.Value),
                    request.AdjustmentAccountId,
                    request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
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
    "/open-items/ap/{openItemId:guid}/adjustment-preview",
    async (Guid openItemId, [AsParameters] OpenItemAdjustmentPreviewLookupQuery query, IApOpenItemRepository openItemRepository, CancellationToken cancellationToken) =>
    {
        var adjustmentType = string.IsNullOrWhiteSpace(query.AdjustmentType) ? "small_balance_adjustment" : query.AdjustmentType;
        var adjustmentDate = query.AdjustmentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var preview = await openItemRepository.GetAdjustmentPreviewAsync(
            new(query.CompanyId),
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
            new(request.CompanyId),
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
            new(query.CompanyId),
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
            new(request.CompanyId),
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
            new(request.CompanyId),
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
        var companyId = new CompanyId(request.CompanyId);
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
        var companyId = new CompanyId(request.CompanyId);
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
            new(query.CompanyId),
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
            new(query.CompanyId),
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
    async (Guid openItemId, Guid requestId, ExecuteOpenItemAdjustmentRequestHttpRequest request, BusinessSessionContextAccessor sessionAccessor, PostApOpenItemAdjustmentCommandHandler handler, CancellationToken cancellationToken) =>
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
                    new(request.CompanyId),
                    openItemId,
                    requestId,
                    new(actorId.Value),
                    request.AdjustmentAccountId,
                    request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

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
            new(query.CompanyId),
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
            new(query.CompanyId),
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
                CompanyId = item.CompanyId.Value,
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
            new(query.CompanyId),
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
            CompanyId = preview.CompanyId.Value,
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
                new(query.CompanyId),
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
                CompanyId = preview.CompanyId.Value,
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
            new(query.CompanyId),
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
            CompanyId = attempt.CompanyId.Value,
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
            new(query.CompanyId),
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
            CompanyId = attempt.CompanyId.Value,
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
            new(query.CompanyId),
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
            CompanyId = request.CompanyId.Value,
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
            new(query.CompanyId),
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
            new(query.CompanyId),
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
            new(query.CompanyId),
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
            CompanyId = request.CompanyId.Value,
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
            new(query.CompanyId),
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
            CompanyId = request.CompanyId.Value,
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
            new(query.CompanyId),
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
            CompanyId = request.CompanyId.Value,
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
        CancellationToken cancellationToken) =>
    {
        var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var actorId = sessionAccessor.Current?.UserId;
        var result = await repository.ExecuteReverseRequestAsync(
            new(query.CompanyId),
            sourceType,
            documentId,
            requestId,
            actorId,
            asOfDate,
            cancellationToken);

        if (result is null)
        {
            return Results.NotFound(new
            {
                message = "Reverse request could not be found in the active company context."
            });
        }

        var request = result.Request;
        var shouldRunLinkedJournalEntryReverse =
            request.JournalEntryId.HasValue &&
            string.Equals(request.ExecutionStatus, "execution_requested", StringComparison.Ordinal) &&
            request.ExecutionCompletedAt is null;

        if (shouldRunLinkedJournalEntryReverse)
        {
            if (!actorId.HasValue)
            {
                return Results.BadRequest(new
                {
                    message = "A business-session user is required before governed reverse execution can reverse the linked journal entry."
                });
            }

            try
            {
                var lifecycleResult = await journalEntryLifecycleWorkflow.ReverseAsync(
                    query.CompanyId,
                    request.JournalEntryId!.Value,
                    actorId.Value,
                    cancellationToken);

                result = await repository.CompleteReverseRequestExecutionAsync(
                        new(query.CompanyId),
                        sourceType,
                        documentId,
                        requestId,
                        actorId,
                        lifecycleResult.CompensationJournalEntryId,
                        lifecycleResult.CompensationDisplayNumber,
                        lifecycleResult.CompensationSourceType,
                        lifecycleResult.LifecycleAt,
                        cancellationToken)
                    ?? result;

                request = result.Request;
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new
                {
                    request.RequestId,
                    CompanyId = request.CompanyId.Value,
                    request.SourceType,
                    SourceTypeLabel = MapDocumentReviewSourceLabel(request.SourceType),
                    Id = request.DocumentId,
                    request.EntityNumber,
                    request.DisplayNumber,
                    request.Status,
                    request.RequestStatus,
                    request.ExecutionStatus,
                    AsOfDate = asOfDate,
                    ExecutionMode = "governed_execution_orchestration",
                    Message = ex.Message
                });
            }
        }

        var payload = new
        {
            request.RequestId,
            CompanyId = request.CompanyId.Value,
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
            new(query.CompanyId),
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
            CompanyId = request.CompanyId.Value,
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
    "/fx-revaluation-batches",
    async ([AsParameters] FxRevaluationBatchListQuery query, IFxRevaluationDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var batches = await repository.ListRecentAsync(
            new(query.CompanyId),
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
    "/invoices/drafts/{documentId:guid}",
    async (Guid documentId, [AsParameters] InvoiceLookupQuery query, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

        return document is null || (document.Status != "draft" && document.Status != "submitted")
            ? Results.NotFound(new { message = "Invoice draft or submitted invoice was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
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

accounting.MapPost(
    "/invoices/drafts",
    async (SaveInvoiceDraftHttpRequest request, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    new(request.CompanyId),
                    new(request.UserId),
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
                        line.UomCode)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
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
                        line.UomCode)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/invoices/drafts/{documentId:guid}/submit",
    async (Guid documentId, SubmitBillDraftHttpRequest request, IInvoiceDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SubmitDraftAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
    "/credit-notes/drafts/{documentId:guid}",
    async (Guid documentId, [AsParameters] CreditNoteLookupQuery query, ICreditNoteDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(new(query.CompanyId), documentId, cancellationToken);
        return document is null || document.Status != "draft"
            ? Results.NotFound(new { message = "Credit note draft was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
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
                    new(request.CompanyId),
                    new(request.UserId),
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
                        line.TaxAmount)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
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
                        line.TaxAmount)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
    "/bills/drafts/{documentId:guid}",
    async (Guid documentId, [AsParameters] BillLookupQuery query, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetForPostingAsync(new(query.CompanyId), documentId, cancellationToken);
        return document is null || (document.Status != "draft" && document.Status != "submitted")
            ? Results.NotFound(new { message = "Bill draft or submitted bill was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
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
                    new(request.CompanyId),
                    new(request.UserId),
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
                        line.PurchaseOrderLineNumber)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
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
                        line.PurchaseOrderLineNumber)).ToArray()),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/bills/drafts/{documentId:guid}/submit",
    async (Guid documentId, SubmitBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.SubmitDraftAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/bills/drafts/{documentId:guid}/cancel",
    async (Guid documentId, SubmitBillDraftHttpRequest request, IBillDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await repository.CancelSubmittedAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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

        var grIrSettlementSummary = await grIrSettlementStore.GetBillSettlementSummaryAsync(
            new(query.CompanyId),
            documentId,
            cancellationToken);

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
            new(query.CompanyId),
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
    "/purchase-orders",
    async (
        [AsParameters] PurchaseOrderListQuery query,
        IPurchaseOrderDocumentRepository repository,
        CancellationToken cancellationToken) =>
    {
        var documents = await repository.ListAsync(new(query.CompanyId), query.Take ?? 50, cancellationToken);
        var summaries = await repository.GetThreeQuantitySummariesAsync(
            new(query.CompanyId),
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
            new(query.CompanyId),
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
        var document = await repository.GetAsync(new(query.CompanyId), documentId, cancellationToken);
        if (document is null)
        {
            return Results.NotFound(new { message = "Purchase order document was not found in the active company context." });
        }

        var summary = await repository.GetThreeQuantitySummaryAsync(new(query.CompanyId), documentId, cancellationToken);
        var purchaseVarianceSummary = await repository.GetPurchaseVarianceSummaryAsync(new(query.CompanyId), documentId, cancellationToken);
        var estimatedAmount = CalculatePurchaseOrderDocumentEstimatedAmount(document);
        return Results.Ok(new
        {
            EstimatedAmount = estimatedAmount,
            document.Id,
            CompanyId = document.CompanyId.Value,
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
        var document = await repository.GetAsync(new(query.CompanyId), documentId, cancellationToken);
        if (document is null)
        {
            return Results.NotFound(new { message = "Purchase order document was not found in the active company context." });
        }

        var entries = await repository.ListLifecycleAuditAsync(
            new(query.CompanyId),
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
        var request = await repository.GetLatestApprovalRequestAsync(new(query.CompanyId), documentId, cancellationToken);
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
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                request.Reason,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                requestId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." })
                : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
        var current = await repository.GetLatestApprovalRequestAsync(new(request.CompanyId), documentId, cancellationToken);
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
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                requestId,
                cancellationToken);

            return result is null
                ? Results.NotFound(new { message = "Purchase order approval request was not found in the active company context." })
                : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
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
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
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
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/approve",
    async (Guid documentId, ApprovePurchaseOrderHttpRequest request, BusinessSessionContextAccessor sessionAccessor, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        var document = await repository.GetAsync(new(request.CompanyId), documentId, cancellationToken);
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
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/quantity-discrepancies/refresh",
    async (Guid documentId, RefreshPurchaseOrderQuantityDiscrepanciesHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var summary = await repository.RefreshQuantityDiscrepanciesAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return summary is null
                ? Results.NotFound(new { message = "Purchase order document was not found in the active company context." })
                : Results.Ok(summary);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/purchase-orders/{documentId:guid}/quantity-discrepancies/review",
    async (Guid documentId, ReviewPurchaseOrderQuantityDiscrepancyHttpRequest request, IPurchaseOrderDocumentRepository repository, CancellationToken cancellationToken) =>
    {
        try
        {
            var summary = await repository.ReviewQuantityDiscrepancyAsync(
                new(request.CompanyId),
                new(request.UserId),
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
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

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
            new(query.CompanyId),
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
            new(query.CompanyId),
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
            new(query.CompanyId),
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
                new(request.CompanyId),
                new(request.UserId),
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
            return Results.BadRequest(new { message = ex.Message });
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
            new(query.CompanyId),
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
            new(query.CompanyId),
            documentId,
            cancellationToken);

        return document is null
            ? Results.NotFound(new { message = "Receipt document was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
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
                    new(request.CompanyId),
                    new(request.UserId),
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
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
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
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/post",
    async (Guid documentId, PostReceiptDraftHttpRequest request, PostReceiptWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.PostAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/inventory-activation/retry",
    async (Guid documentId, PostReceiptDraftHttpRequest request, PostReceiptWorkflow workflow, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await workflow.PostAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

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
            return Results.BadRequest(new { message = ex.Message });
        }
    });

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
            return Results.BadRequest(new { message = ex.Message });
        }
    });

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
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/refresh",
    async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrApSettlementControlStore grIrSettlementStore, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await grIrSettlementStore.RefreshReceiptSettlementControlAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/journal-reconciliation/refresh",
    async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrApSettlementControlStore grIrSettlementStore, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await grIrSettlementStore.RefreshReceiptSettlementJournalReconciliationAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-settlement/purchase-variance/refresh",
    async (Guid documentId, PostReceiptDraftHttpRequest request, IReceiptGrIrApSettlementControlStore grIrSettlementStore, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await grIrSettlementStore.RefreshReceiptSettlementVarianceControlAsync(
                new(request.CompanyId),
                new(request.UserId),
                documentId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapGet(
    "/receipts/{documentId:guid}/grir-settlement/purchase-variance/lines",
    async (
        Guid documentId,
        [AsParameters] ReceiptLookupQuery query,
        IReceiptGrIrApSettlementControlStore grIrSettlementStore,
        CancellationToken cancellationToken) =>
    {
        var result = await grIrSettlementStore.ListReceiptPurchaseVarianceLinesAsync(
            new(query.CompanyId),
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
            new(query.CompanyId),
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
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    request.SettlementAmountBase,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    settlementBatchId,
                    request.IdempotencyKey),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    settlementBatchId),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    settlementBatchId),
                cancellationToken);

            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });

accounting.MapPost(
    "/receipts/{documentId:guid}/grir-bridge/post",
    async (Guid documentId, PostReceiptGrIrBridgeHttpRequest request, PostReceiptGrIrCommandHandler handler, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await handler.HandleAsync(
                new PostReceiptGrIrCommand(
                    new(request.CompanyId),
                    new(request.UserId),
                    documentId,
                    request.GrIrClearingAccountId,
                    request.IdempotencyKey),
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
        var document = await repository.GetForPostingAsync(new(query.CompanyId), documentId, cancellationToken);
        return document is null || document.Status != "draft"
            ? Results.NotFound(new { message = "Vendor credit draft was not found in the active company context." })
            : Results.Ok(new
            {
                document.Id,
                CompanyId = document.CompanyId.Value,
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
                    new(request.CompanyId),
                    new(request.UserId),
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
            return Results.BadRequest(new { message = ex.Message });
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
                    new(request.CompanyId),
                    new(request.UserId),
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
            return Results.BadRequest(new { message = ex.Message });
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

static IResult? RequireOpenItemAdjustmentApprovalAuthority(
    BusinessSessionContext? session,
    OpenItemAdjustmentRequestRecord request,
    string openItemLabel,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentApproval(
        session,
        openItemLabel,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new OpenItemAdjustmentRequestTransitionResult(
                request,
                transitionCode,
                decision.OutcomeCode,
                decision.Message),
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequireOpenItemAdjustmentAccountMappingManagementAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentAccountMappingManagement(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequireGrIrClearingAccountPolicyManagementAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluateGrIrClearingAccountPolicyManagement(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequireGrIrSettlementExecutionAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluateGrIrSettlementExecution(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderApprovalAuthority(
    BusinessSessionContext? session,
    string transitionCode,
    decimal? estimatedOrderAmount = null)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApproval(
        session,
        transitionCode,
        estimatedOrderAmount);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                estimatedOrderAmount,
                approvalThresholdAmount = BusinessApprovalAuthority.PurchaseOrderApprovalGovernanceThresholdAmount,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderReleaseAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderRelease(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderAmendmentAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderAmendment(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderApprovalReversalAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApprovalReversal(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderCloseAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderClose(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static IResult? RequirePurchaseOrderCancelAuthority(
    BusinessSessionContext? session,
    string transitionCode)
{
    var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderCancel(
        session,
        transitionCode);

    return decision.Allowed
        ? null
        : Results.Json(
            new
            {
                transitionCode,
                outcomeCode = decision.OutcomeCode,
                message = decision.Message
            },
            statusCode: StatusCodes.Status403Forbidden);
}

static decimal? CalculatePurchaseOrderListEstimatedAmount(PurchaseOrderDocumentListItem document) => null;

static decimal? CalculatePurchaseOrderDocumentEstimatedAmount(PurchaseOrderDocument document) =>
    document.PurchaseOrderLines.Any(static line => !line.UnitCost.HasValue)
        ? null
        : document.PurchaseOrderLines.Sum(static line => line.OrderedQuantity * line.UnitCost!.Value);

static object BuildPurchaseOrderApprovalAuthoritySummary(decimal? estimatedOrderAmount) =>
    new
    {
        EstimatedOrderAmount = estimatedOrderAmount,
        ThresholdAmount = BusinessApprovalAuthority.PurchaseOrderApprovalGovernanceThresholdAmount,
        RequiresGovernanceApproval = BusinessApprovalAuthority.RequiresPurchaseOrderGovernanceApproval(estimatedOrderAmount),
        Summary = !estimatedOrderAmount.HasValue
            ? "Estimated purchase order amount is unavailable, so the temporary threshold does not add an approval block yet."
            : BusinessApprovalAuthority.RequiresPurchaseOrderGovernanceApproval(estimatedOrderAmount)
            ? "Purchase order approval is above the temporary governance threshold and requires owner or governance authority."
            : "Purchase order approval is within the temporary approver threshold."
    };

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
        "invoice_reversal" => "Invoice Reversal",
        "credit_note_reversal" => "Credit Note Reversal",
        "bill_reversal" => "Bill Reversal",
        "vendor_credit_reversal" => "Vendor Credit Reversal",
        "receive_payment_reversal" => "Receive Payment Reversal",
        "credit_application_reversal" => "Credit Application Reversal",
        "pay_bill_reversal" => "Pay Bill Reversal",
        "vendor_credit_application_reversal" => "Vendor Credit Application Reversal",
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

static string BuildInvoiceCoverageSummary(InventoryInvoiceShipmentPostingGateSnapshot snapshot) =>
    snapshot.InvoiceCoverageStatus switch
    {
        "no_inventory_handoff" => "No shipped/invoiced coverage lane is active for this invoice.",
        "no_shipment" => "Shipment truth has not started yet, so nothing is formally invoiced against shipped quantity.",
        "not_invoiced" => $"Shipment truth exists, but {snapshot.RemainingToInvoiceQuantity:N2} shipped quantity still has no formal AR coverage.",
        "partially_invoiced" => $"{snapshot.RemainingToInvoiceQuantity:N2} shipped quantity is still waiting for formal AR coverage.",
        "fully_invoiced" => "Current shipped quantity is fully covered by posted AR truth.",
        "over_invoiced" => "Posted invoice truth currently exceeds shipped quantity and should move into discrepancy review.",
        _ => "Invoice coverage truth has not been evaluated yet."
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
        PartyId = line.PartyId,
        PostingRole = line.PostingRole,
        SourceLineNumber = line.SourceLineNumber
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
        "invoice_reversal" => "Invoice Reversal",
        "credit_note_reversal" => "Credit Note Reversal",
        "bill_reversal" => "Bill Reversal",
        "vendor_credit_reversal" => "Vendor Credit Reversal",
        "receive_payment_reversal" => "Receive Payment Reversal",
        "credit_application_reversal" => "Credit Application Reversal",
        "pay_bill_reversal" => "Pay Bill Reversal",
        "vendor_credit_application_reversal" => "Vendor Credit Application Reversal",
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
