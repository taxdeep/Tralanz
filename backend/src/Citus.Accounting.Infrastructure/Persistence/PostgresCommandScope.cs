using Npgsql;

namespace Citus.Accounting.Infrastructure.Persistence;

internal sealed class PostgresCommandScope : IAsyncDisposable
{
    private readonly bool _ownsConnection;
    private readonly bool _ownsTransaction;
    private bool _transactionCompleted;

    private PostgresCommandScope(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        bool ownsConnection,
        bool ownsTransaction = false)
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
            return new PostgresCommandScope(current.Connection, current.Transaction, ownsConnection: false);
        }

        var connection = await connections.OpenConnectionAsync(cancellationToken);
        return new PostgresCommandScope(connection, transaction: null, ownsConnection: true);
    }

    public static async Task<PostgresCommandScope> CreateTransactionalAsync(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor,
        CancellationToken cancellationToken)
    {
        var current = executionContextAccessor.Current;
        if (current is not null)
        {
            if (current.Transaction is not null)
            {
                return new PostgresCommandScope(current.Connection, current.Transaction, ownsConnection: false);
            }

            var currentTransaction = await current.Connection.BeginTransactionAsync(cancellationToken);
            return new PostgresCommandScope(
                current.Connection,
                currentTransaction,
                ownsConnection: false,
                ownsTransaction: true);
        }

        var connection = await connections.OpenConnectionAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return new PostgresCommandScope(
            connection,
            transaction,
            ownsConnection: true,
            ownsTransaction: true);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (!_ownsTransaction || Transaction is null || _transactionCompleted)
        {
            return;
        }

        await Transaction.CommitAsync(cancellationToken);
        _transactionCompleted = true;
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

    public async ValueTask DisposeAsync()
    {
        if (_ownsTransaction && Transaction is not null)
        {
            if (!_transactionCompleted)
            {
                await Transaction.RollbackAsync(CancellationToken.None);
            }

            await Transaction.DisposeAsync();
        }

        if (_ownsConnection)
        {
            await Connection.DisposeAsync();
        }
    }
}
