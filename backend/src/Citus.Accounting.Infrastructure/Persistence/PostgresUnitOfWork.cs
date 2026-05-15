using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresUnitOfWork : IUnitOfWork
{
    private const int MaxAttempts = 3;

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresUnitOfWork(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_executionContextAccessor.Current is not null)
        {
            return await action(cancellationToken);
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            _executionContextAccessor.Current = new PostgresExecutionContext(connection, transaction);

            try
            {
                var result = await action(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (PostgresException ex) when (IsTransientConcurrencyFailure(ex) && attempt < MaxAttempts)
            {
                await RollbackBestEffortAsync(transaction, CancellationToken.None);
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch
            {
                await RollbackBestEffortAsync(transaction, CancellationToken.None);
                throw;
            }
            finally
            {
                _executionContextAccessor.Current = null;
            }
        }

        throw new InvalidOperationException("The PostgreSQL unit of work retry loop exited unexpectedly.");
    }

    private static bool IsTransientConcurrencyFailure(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure;

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);

    private static async Task RollbackBestEffortAsync(
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch
        {
            // Preserve the original database failure; rollback can fail if the connection is already broken.
        }
    }
}
