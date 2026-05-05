using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// M6 iter 4: builds a <see cref="DropShipClearingWriteOffDocument"/>
/// for a one-shot variance write-off against the Drop-ship Clearing
/// account for a specific item. The repository re-reads the live
/// per-item residual (same SQL the aging reader uses) and verifies it
/// matches the operator's expected amount before producing the
/// document — protects against concurrent-activity sign flips.
/// </summary>
public interface IDropShipClearingWriteOffRepository
{
    Task<DropShipClearingWriteOffDocument> PrepareAsync(
        CompanyId companyId,
        UserId userId,
        Guid itemId,
        decimal expectedNetClearingBase,
        string? memo,
        CancellationToken cancellationToken);
}
