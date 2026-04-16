using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

public sealed record PostArOpenItemAdjustmentCommand(
    CompanyId CompanyId,
    Guid OpenItemId,
    Guid RequestId,
    UserId UserId,
    Guid AdjustmentAccountId,
    DateOnly AsOfDate,
    string? IdempotencyKey);

public sealed record PostArOpenItemAdjustmentCommandResult(
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt,
    decimal AdjustmentAmountTx,
    decimal AdjustmentAmountBase);
