using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Application.Commands;

public sealed class PostReceiptGrIrCommandHandler
{
    private readonly IReceiptGrIrBridgeStore _bridgeStore;
    private readonly IReceiptGrIrClearingAccountPolicyRepository _clearingAccountPolicyRepository;
    private readonly IReceiptGrIrPostingRepository _postingRepository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostReceiptGrIrCommandHandler(
        IReceiptGrIrBridgeStore bridgeStore,
        IReceiptGrIrClearingAccountPolicyRepository clearingAccountPolicyRepository,
        IReceiptGrIrPostingRepository postingRepository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _bridgeStore = bridgeStore ?? throw new ArgumentNullException(nameof(bridgeStore));
        _clearingAccountPolicyRepository = clearingAccountPolicyRepository ?? throw new ArgumentNullException(nameof(clearingAccountPolicyRepository));
        _postingRepository = postingRepository ?? throw new ArgumentNullException(nameof(postingRepository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostReceiptGrIrCommandResult> HandleAsync(
        PostReceiptGrIrCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            await _bridgeStore.RefreshReceiptGrIrBridgeAsync(
                command.CompanyId,
                command.UserId,
                command.ReceiptDocumentId,
                ct);

            var grIrClearingAccountId = command.GrIrClearingAccountId is { } explicitAccountId &&
                                         explicitAccountId != Guid.Empty
                ? explicitAccountId
                : await _clearingAccountPolicyRepository.GetDefaultGrIrClearingAccountIdAsync(
                    command.CompanyId,
                    ct);
            if (grIrClearingAccountId is null || grIrClearingAccountId.Value == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "GR/IR clearing account is required. Configure a company default policy or pass an explicit account id for this posting.");
            }

            var document = await _postingRepository.PreparePostingDocumentAsync(
                command.CompanyId,
                command.UserId,
                command.ReceiptDocumentId,
                grIrClearingAccountId.Value,
                ct);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"receipt-grir:{command.CompanyId}:{document.Id}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);
            await _postingRepository.CompletePostingAsync(
                command.CompanyId,
                command.UserId,
                document.Id,
                result.JournalEntryId,
                result.JournalEntryDisplayNumber,
                ct);

            return PostReceiptGrIrCommandResult.FromPostingResult(
                command.ReceiptDocumentId,
                document.Id,
                result);
        }, cancellationToken);
    }
}
