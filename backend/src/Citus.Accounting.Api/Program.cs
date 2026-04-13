using Citus.Accounting.Api;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;

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
builder.Services.AddScoped<IFxSnapshotRepository, PostgresFxSnapshotRepository>();
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

var accounting = app.MapGroup("/accounting");

app.MapGet("/", () => Results.Ok(new
{
    service = "Citus.Accounting.Api",
    status = "settlement-draft-preparation-wired",
    authority = "CITUS_PRODUCT_ENGINEERING_AUTHORITY.md",
    storage = "PostgreSQL"
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
        "Domain",
        "Application",
        "Infrastructure",
        "Api"
    },
    postingRule = "All formal accounting must go through the Posting Engine."
}));

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
            document.DocumentDate,
            TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
            BaseCurrencyCode = document.BaseCurrencyCode.Value,
            FxSnapshotId = document.FxSnapshot.SnapshotId == Guid.Empty ? (Guid?)null : document.FxSnapshot.SnapshotId,
            FxRate = document.FxSnapshot.Rate,
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
