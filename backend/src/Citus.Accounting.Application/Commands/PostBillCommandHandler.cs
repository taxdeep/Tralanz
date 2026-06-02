using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class PostBillCommandHandler
{
    private readonly IBillDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly IApOpenItemRepository _openItems;
    private readonly IBillReceiptMatchingRepository _billReceiptMatchingRepository;
    private readonly IPurchaseOrderDocumentRepository _purchaseOrders;
    private readonly IUnitOfWork _unitOfWork;

    public PostBillCommandHandler(
        IBillDocumentRepository documents,
        IPostingEngine postingEngine,
        IApOpenItemRepository openItems,
        IBillReceiptMatchingRepository billReceiptMatchingRepository,
        IPurchaseOrderDocumentRepository purchaseOrders,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _openItems = openItems ?? throw new ArgumentNullException(nameof(openItems));
        _billReceiptMatchingRepository = billReceiptMatchingRepository ?? throw new ArgumentNullException(nameof(billReceiptMatchingRepository));
        _purchaseOrders = purchaseOrders ?? throw new ArgumentNullException(nameof(purchaseOrders));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostBillCommandResult> HandleAsync(
        PostBillCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException("Bill document was not found in the active company context.");
            }

            // The JE writer flips a bill draft → posted (its source-status
            // claim matches status='draft'), so a draft posts directly from
            // the detail page — mirroring the invoice. 'submitted' is also
            // accepted for any approval-flow caller that pre-submits.
            if (!string.Equals(document.Status, "draft", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(document.Status, "submitted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only draft or submitted bills can be posted.");
            }

            await _purchaseOrders.ValidateBillAnchorsForPostingAsync(
                command.CompanyId,
                command.DocumentId,
                ct);

            var receiptHandoffSummary = await _billReceiptMatchingRepository.GetBillLaneSummaryAsync(
                command.CompanyId,
                command.DocumentId,
                ct);

            if (!BillReceiptPostingGatePolicy.AllowsBillPost(receiptHandoffSummary.MatchStatus))
            {
                throw new InvalidOperationException(
                    BillReceiptPostingGatePolicy.GetBlockedPostMessage(receiptHandoffSummary));
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"bill:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                acceptedFxSnapshotId,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            var originalAmountBase = Math.Round(
                document.TotalAmount * (document.FxSnapshot?.Rate ?? 1m),
                2,
                MidpointRounding.ToEven);

            await _openItems.EnsureForBillAsync(document, originalAmountBase, ct);
            return PostBillCommandResult.FromPostingResult(result);
        }, cancellationToken);
    }
}
