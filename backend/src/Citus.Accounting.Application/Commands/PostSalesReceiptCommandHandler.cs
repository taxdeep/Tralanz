using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// Mirror of <see cref="PostInvoiceCommandHandler"/> with two
/// surgical removals:
///   • No <c>IArOpenItemRepository</c> dependency. Sales receipts do
///     not open an AR row — the cash already arrived.
///   • No inventory shipment handoff gate. The shipment policy is
///     invoice-flow-specific (B2B fulfilment); cash sales settle on
///     the spot regardless of inventory state. (If a future feature
///     wants to gate sales-receipt posting on stock availability,
///     it lands as a separate gate, not the same one.)
/// </summary>
public sealed class PostSalesReceiptCommandHandler
{
    private readonly ISalesReceiptDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostSalesReceiptCommandHandler(
        ISalesReceiptDocumentRepository documents,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostSalesReceiptCommandResult> HandleAsync(
        PostSalesReceiptCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException(
                    "Sales receipt document was not found in the active company context.");
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            // Idempotency key shape mirrors the invoice flow. Default
            // is deterministic from (company, document) so a retry
            // with the same draft id collapses to one ledger write at
            // the JournalEntryWriter level — no double-post even
            // without an explicit operator-supplied key.
            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"sales_receipt:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                acceptedFxSnapshotId,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);
            return PostSalesReceiptCommandResult.FromPostingResult(result);
        }, cancellationToken);
    }
}
