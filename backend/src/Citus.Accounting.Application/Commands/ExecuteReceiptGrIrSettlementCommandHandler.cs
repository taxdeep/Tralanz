using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

public sealed class ExecuteReceiptGrIrSettlementCommandHandler
{
    private readonly IReceiptGrIrApSettlementControlStore _settlementControlStore;
    private readonly IUnitOfWork _unitOfWork;

    public ExecuteReceiptGrIrSettlementCommandHandler(
        IReceiptGrIrApSettlementControlStore settlementControlStore,
        IUnitOfWork unitOfWork)
    {
        _settlementControlStore = settlementControlStore ?? throw new ArgumentNullException(nameof(settlementControlStore));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<ReceiptGrIrApSettlementExecutionResult> HandleAsync(
        ExecuteReceiptGrIrSettlementCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(
            ct => _settlementControlStore.ExecuteReceiptSettlementAsync(
                command.CompanyId,
                command.UserId,
                command.ReceiptDocumentId,
                new ReceiptGrIrApSettlementExecutionRequest(
                    command.SettlementAmountBase,
                    command.IdempotencyKey),
                ct),
            cancellationToken);
    }
}

public sealed record ExecuteReceiptGrIrSettlementCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid ReceiptDocumentId,
    decimal? SettlementAmountBase,
    string? IdempotencyKey);

public sealed class ClearReceiptGrIrSettlementOpenItemCommandHandler
{
    private readonly IReceiptGrIrApSettlementControlStore _settlementControlStore;
    private readonly IUnitOfWork _unitOfWork;

    public ClearReceiptGrIrSettlementOpenItemCommandHandler(
        IReceiptGrIrApSettlementControlStore settlementControlStore,
        IUnitOfWork unitOfWork)
    {
        _settlementControlStore = settlementControlStore ?? throw new ArgumentNullException(nameof(settlementControlStore));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<ReceiptGrIrApOpenItemClearingResult> HandleAsync(
        ClearReceiptGrIrSettlementOpenItemCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(
            ct => _settlementControlStore.ClearReceiptSettlementOpenItemsAsync(
                command.CompanyId,
                command.UserId,
                command.ReceiptDocumentId,
                command.SettlementBatchId,
                ct),
            cancellationToken);
    }
}

public sealed record ClearReceiptGrIrSettlementOpenItemCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid ReceiptDocumentId,
    Guid SettlementBatchId);

public sealed class ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler
{
    private readonly IReceiptGrIrApSettlementControlStore _settlementControlStore;
    private readonly IUnitOfWork _unitOfWork;

    public ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler(
        IReceiptGrIrApSettlementControlStore settlementControlStore,
        IUnitOfWork unitOfWork)
    {
        _settlementControlStore = settlementControlStore ?? throw new ArgumentNullException(nameof(settlementControlStore));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<ReceiptGrIrApOpenItemClearingReversalResult> HandleAsync(
        ReverseReceiptGrIrSettlementOpenItemClearingCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(
            ct => _settlementControlStore.ReverseReceiptSettlementOpenItemClearingAsync(
                command.CompanyId,
                command.UserId,
                command.ReceiptDocumentId,
                command.SettlementBatchId,
                ct),
            cancellationToken);
    }
}

public sealed record ReverseReceiptGrIrSettlementOpenItemClearingCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid ReceiptDocumentId,
    Guid SettlementBatchId);
