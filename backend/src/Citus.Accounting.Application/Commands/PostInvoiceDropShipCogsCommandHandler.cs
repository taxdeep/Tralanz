using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// M6 iter 3 — invoice → Dr COGS / Cr Drop-ship Clearing for any
/// drop-ship line on the invoice. Mirror of
/// <see cref="PostSalesIssueCogsCommandHandler"/> but pulls the cost
/// basis from the item master (default_purchase_price) rather than the
/// inventory cost layers (which drop-ship items never touch).
///
/// Idempotency is journal-layer:
/// (source_type='invoice_drop_ship_cogs', source_id=invoiceId).
/// Re-running on the same invoice returns the existing JE rather than
/// double-posting. Invoices with no drop-ship lines return a NoOp
/// result and produce no JE.
/// </summary>
public sealed class PostInvoiceDropShipCogsCommandHandler
{
    private readonly IInvoiceDropShipCogsPostingRepository _postingRepository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostInvoiceDropShipCogsCommandHandler(
        IInvoiceDropShipCogsPostingRepository postingRepository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _postingRepository = postingRepository ?? throw new ArgumentNullException(nameof(postingRepository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostInvoiceDropShipCogsCommandResult> HandleAsync(
        PostInvoiceDropShipCogsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var preparation = await _postingRepository.PreparePostingDocumentAsync(
                command.CompanyId,
                command.UserId,
                command.InvoiceDocumentId,
                ct);

            // Idempotent re-run: existing JE found.
            if (preparation.Document is null && preparation.ExistingJournalEntryId is { } existingId)
            {
                return new PostInvoiceDropShipCogsCommandResult(
                    InvoiceDocumentId: command.InvoiceDocumentId,
                    JournalEntryId: existingId,
                    JournalEntryDisplayNumber: preparation.ExistingJournalEntryDisplayNumber ?? string.Empty,
                    AlreadyPosted: true,
                    NoOp: false,
                    TotalAmountBase: 0m);
            }

            // No drop-ship lines on the invoice — nothing to post.
            if (preparation.Document is null)
            {
                return new PostInvoiceDropShipCogsCommandResult(
                    InvoiceDocumentId: command.InvoiceDocumentId,
                    JournalEntryId: null,
                    JournalEntryDisplayNumber: null,
                    AlreadyPosted: false,
                    NoOp: true,
                    TotalAmountBase: 0m);
            }

            var document = preparation.Document;

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"invoice-drop-ship-cogs:{command.CompanyId.Value}:{command.InvoiceDocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            return new PostInvoiceDropShipCogsCommandResult(
                InvoiceDocumentId: command.InvoiceDocumentId,
                JournalEntryId: result.JournalEntryId,
                JournalEntryDisplayNumber: result.JournalEntryDisplayNumber,
                AlreadyPosted: false,
                NoOp: false,
                TotalAmountBase: document.TotalAmountBase);
        }, cancellationToken);
    }
}
