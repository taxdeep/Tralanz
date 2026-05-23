using Citus.Modules.Inventory.Application.Contracts;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

/// <summary>
/// PostgreSQL implementation of <see cref="IInventoryReceiptUnitOfWork"/>.
/// Opens one Npgsql connection + transaction, publishes it on the
/// <see cref="InventoryReceiptExecutionContextAccessor"/>, runs the
/// caller's action, then commits — or rolls back on any unhandled
/// exception. The three receipt stores each check the accessor and
/// join the ambient tx when it's set, so activation + valuation +
/// emission all commit (or all roll back) together.
///
/// Re-entrancy: if a caller invokes ExecuteAsync inside an already-
/// open scope, the inner call runs without opening a new tx — the
/// outer scope's commit/rollback governs the whole tree.
///
/// Retry on transient concurrency failures (deadlock /
/// serialization_failure) is intentionally NOT implemented here; the
/// receipt workflow is operator-driven and doesn't share rows with
/// concurrent posters in the same tx scope. If contention ever
/// becomes a real concern, lift the retry pattern from
/// PostgresUnitOfWork (accounting layer).
/// </summary>
public sealed class PostgreSqlInventoryReceiptUnitOfWork : IInventoryReceiptUnitOfWork
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly InventoryReceiptExecutionContextAccessor _accessor;

    public PostgreSqlInventoryReceiptUnitOfWork(
        PostgreSqlConnectionFactory connections,
        InventoryReceiptExecutionContextAccessor accessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Nested call: an outer ExecuteAsync is already in flight and
        // owns the tx. Just run the inner action; the outer commits.
        if (_accessor.Current is not null)
        {
            await action(cancellationToken);
            return;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        _accessor.Current = new InventoryReceiptExecutionContext(connection, transaction);

        try
        {
            await action(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // Roll back explicitly so the rollback completes before
            // the `await using` disposes the tx; this surfaces a
            // PostgresException from rollback into the caller's
            // stack rather than the dispose path.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original failure; rollback can fail
                // if the connection is already broken.
            }
            throw;
        }
        finally
        {
            _accessor.Current = null;
        }
    }
}
