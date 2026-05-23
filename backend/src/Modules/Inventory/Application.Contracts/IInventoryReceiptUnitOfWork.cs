namespace Citus.Modules.Inventory.Application.Contracts;

/// <summary>
/// M4 (AUDIT_2026-05-20 P2-10): atomic boundary for the three-step
/// inventory cycle a Receipt post triggers — activation,
/// valuation, and cost-layer emission. The workflow wraps those three
/// steps in a single <see cref="ExecuteAsync"/>; the underlying
/// implementation opens one Npgsql transaction and threads it through
/// the three stores via an ambient AsyncLocal accessor, so a failure
/// at valuation or emission rolls back the activation rows too. That
/// closes the P2-10 partial-state risk where a posted receipt could
/// land "activated but un-emitted", producing wrong COGS on any
/// later sales-issue that consumed cost layers from earlier receipts.
///
/// Re-entrancy: a nested ExecuteAsync on a context that already has
/// an ambient tx simply runs the inner action without opening a new
/// tx — the outer caller still owns commit/rollback.
/// </summary>
public interface IInventoryReceiptUnitOfWork
{
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}
