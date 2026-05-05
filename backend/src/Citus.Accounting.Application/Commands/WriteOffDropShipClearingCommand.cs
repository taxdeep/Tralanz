using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// M6 iter 4: operator-driven write-off of a drop-ship clearing residual
/// for one item. Posts a one-shot Dr/Cr pair that zeros the clearing
/// balance for that item against the Purchase Price Variance account.
/// The expected residual must match the live read at write-off time so
/// concurrent activity (a new bill / invoice posting between the
/// operator's "view aging" and "click write-off" actions) doesn't
/// silently flip the sign or amount.
/// </summary>
public sealed record WriteOffDropShipClearingCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid ItemId,
    decimal ExpectedNetClearingBase,
    string? Memo,
    string? IdempotencyKey = null);

public sealed record WriteOffDropShipClearingCommandResult(
    Guid ItemId,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    decimal NetClearingAmountBase);
