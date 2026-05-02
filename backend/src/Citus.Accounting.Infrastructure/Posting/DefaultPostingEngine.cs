using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Infrastructure.Posting;

public sealed class DefaultPostingEngine : IPostingEngine
{
    private readonly IPostingValidator _validator;
    private readonly ITaxEngine _taxEngine;
    private readonly IFxResolutionService _fxResolutionService;
    private readonly IPostingFragmentBuilder _fragmentBuilder;
    private readonly IJournalAggregator _journalAggregator;
    private readonly IJournalEntryWriter _writer;

    public DefaultPostingEngine(
        IPostingValidator validator,
        ITaxEngine taxEngine,
        IFxResolutionService fxResolutionService,
        IPostingFragmentBuilder fragmentBuilder,
        IJournalAggregator journalAggregator,
        IJournalEntryWriter writer)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _taxEngine = taxEngine ?? throw new ArgumentNullException(nameof(taxEngine));
        _fxResolutionService = fxResolutionService ?? throw new ArgumentNullException(nameof(fxResolutionService));
        _fragmentBuilder = fragmentBuilder ?? throw new ArgumentNullException(nameof(fragmentBuilder));
        _journalAggregator = journalAggregator ?? throw new ArgumentNullException(nameof(journalAggregator));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public async Task<PostingResult> PostAsync(
        IPostingDocument document,
        PostingContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        await _validator.ValidateAsync(document, context, cancellationToken);

        var taxResult = await _taxEngine.CalculateAsync(document, cancellationToken);

        var embeddedFxSnapshot = TryGetEmbeddedFxSnapshot(document);
        FxResolutionResult fxResult;
        if (embeddedFxSnapshot is not null && document.TransactionCurrencyCode != document.BaseCurrencyCode)
        {
            if (context.AcceptedFxSnapshotId.HasValue && context.AcceptedFxSnapshotId.Value != embeddedFxSnapshot.SnapshotId)
            {
                throw new InvalidOperationException(
                    "The posting request cannot override the FX snapshot stored on the source document.");
            }

            fxResult = new FxResolutionResult(embeddedFxSnapshot, new[] { "Source-document FX snapshot applied." });
        }
        else
        {
            fxResult = await _fxResolutionService.ResolveAsync(
                new FxResolutionRequest(
                    context.CompanyId,
                    document.BaseCurrencyCode,
                    document.TransactionCurrencyCode,
                    document.DocumentDate,
                    context.AcceptedFxSnapshotId,
                    document.SourceType),
                cancellationToken);
        }

        var fragments = await _fragmentBuilder.BuildAsync(document, taxResult, fxResult, cancellationToken);
        var draft = _journalAggregator.Aggregate(document, fragments, fxResult);
        var writeResult = await _writer.WriteAsync(draft, context, cancellationToken);

        return new PostingResult(
            writeResult.JournalEntryId,
            writeResult.JournalEntryDisplayNumber,
            "posted",
            DateTimeOffset.UtcNow,
            Array.Empty<string>());
    }

    private static Citus.Accounting.Domain.Currencies.FxSnapshotRef? TryGetEmbeddedFxSnapshot(IPostingDocument document) =>
        document switch
        {
            ManualJournalDocument manualJournal => manualJournal.FxSnapshot,
            InvoiceDocument invoice => invoice.FxSnapshot,
            SalesReceiptDocument salesReceipt => salesReceipt.FxSnapshot,
            CreditNoteDocument creditNote => creditNote.FxSnapshot,
            BillDocument bill => bill.FxSnapshot,
            VendorCreditDocument vendorCredit => vendorCredit.FxSnapshot,
            CreditApplicationDocument creditApplication => creditApplication.FxSnapshot,
            ReceivePaymentDocument receivePayment => receivePayment.FxSnapshot,
            VendorCreditApplicationDocument vendorCreditApplication => vendorCreditApplication.FxSnapshot,
            PayBillDocument payBill => payBill.FxSnapshot,
            FxRevaluationDocument fxRevaluation => fxRevaluation.FxSnapshot,
            _ => null
        };
}
