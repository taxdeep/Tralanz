using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Application.Commands;

public sealed class PostBillCommandHandler
{
    private readonly IBillDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly IApOpenItemRepository _openItems;
    private readonly IInventoryReceiptStore _inventoryReceiptStore;
    private readonly IUnitOfWork _unitOfWork;

    public PostBillCommandHandler(
        IBillDocumentRepository documents,
        IPostingEngine postingEngine,
        IApOpenItemRepository openItems,
        IInventoryReceiptStore inventoryReceiptStore,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _openItems = openItems ?? throw new ArgumentNullException(nameof(openItems));
        _inventoryReceiptStore = inventoryReceiptStore ?? throw new ArgumentNullException(nameof(inventoryReceiptStore));
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

            if (!string.Equals(document.Status, "submitted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only submitted bills can be posted.");
            }

            var receiptHandoffSummary = await _inventoryReceiptStore.GetBillHandoffSummaryAsync(
                command.CompanyId.Value,
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
