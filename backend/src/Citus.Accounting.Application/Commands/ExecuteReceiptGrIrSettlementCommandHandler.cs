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
