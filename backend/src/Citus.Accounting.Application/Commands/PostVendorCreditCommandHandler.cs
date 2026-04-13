using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class PostVendorCreditCommandHandler
{
    private readonly IVendorCreditDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly IApOpenItemRepository _openItems;
    private readonly IUnitOfWork _unitOfWork;

    public PostVendorCreditCommandHandler(
        IVendorCreditDocumentRepository documents,
        IPostingEngine postingEngine,
        IApOpenItemRepository openItems,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _openItems = openItems ?? throw new ArgumentNullException(nameof(openItems));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostVendorCreditCommandResult> HandleAsync(
        PostVendorCreditCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException("Vendor credit document was not found in the active company context.");
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"vendor-credit:{command.CompanyId}:{command.DocumentId}"
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

            await _openItems.EnsureForVendorCreditAsync(document, originalAmountBase, ct);
            return PostVendorCreditCommandResult.FromPostingResult(result);
        }, cancellationToken);
    }
}
