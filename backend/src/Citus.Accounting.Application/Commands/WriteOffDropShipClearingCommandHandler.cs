using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

public sealed class WriteOffDropShipClearingCommandHandler
{
    private readonly IDropShipClearingWriteOffRepository _repository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public WriteOffDropShipClearingCommandHandler(
        IDropShipClearingWriteOffRepository repository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<WriteOffDropShipClearingCommandResult> HandleAsync(
        WriteOffDropShipClearingCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _repository.PrepareAsync(
                command.CompanyId,
                command.UserId,
                command.ItemId,
                command.ExpectedNetClearingBase,
                command.Memo,
                ct);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"drop-ship-clearing-writeoff:{command.CompanyId.Value}:{document.Id}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            return new WriteOffDropShipClearingCommandResult(
                ItemId: command.ItemId,
                JournalEntryId: result.JournalEntryId,
                JournalEntryDisplayNumber: result.JournalEntryDisplayNumber,
                NetClearingAmountBase: document.NetClearingAmountBase);
        }, cancellationToken);
    }
}
