using Npgsql;

namespace Citus.Accounting.Infrastructure.Persistence;

internal sealed class PostgresCommandScope : IAsyncDisposable
{
    private readonly bool _ownsConnection;
    private readonly bool _ownsTransaction;
    private bool _committed;

    private PostgresCommandScope(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        bool ownsConnection,
        bool ownsTransaction)
    {
        Connection = connection;
        Transaction = transaction;
        _ownsConnection = ownsConnection;
        _ownsTransaction = ownsTransaction;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction? Transaction { get; }

    public static async Task<PostgresCommandScope> CreateAsync(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor,
        CancellationToken cancellationToken)
    {
        var current = executionContextAccessor.Current;
        if (current is not null)
        {
            return new PostgresCommandScope(current.Connection, current.Transaction, ownsConnection: false, ownsTransaction: false);
        }

        var connection = await connections.OpenConnectionAsync(cancellationToken);
        return new PostgresCommandScope(connection, transaction: null, ownsConnection: true, ownsTransaction: false);
    }

    public static async Task<PostgresCommandScope> CreateTransactionalAsync(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor,
        CancellationToken cancellationToken)
    {
        var current = executionContextAccessor.Current;
        if (current is not null)
        {
            return new PostgresCommandScope(current.Connection, current.Transaction, ownsConnection: false, ownsTransaction: false);
        }

        var connection = await connections.OpenConnectionAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return new PostgresCommandScope(connection, transaction, ownsConnection: true, ownsTransaction: true);
    }

    public NpgsqlCommand CreateCommand(string commandText)
    {
        var command = Connection.CreateCommand();
        command.CommandText = commandText;

        if (Transaction is not null)
        {
            command.Transaction = Transaction;
        }

        return command;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_ownsTransaction && Transaction is not null && !_committed)
        {
            await Transaction.CommitAsync(cancellationToken);
            _committed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsTransaction && Transaction is not null)
        {
            if (!_committed)
            {
                await Transaction.RollbackAsync();
            }

            await Transaction.DisposeAsync();
        }

        if (_ownsConnection)
        {
            await Connection.DisposeAsync();
        }
    }
}
