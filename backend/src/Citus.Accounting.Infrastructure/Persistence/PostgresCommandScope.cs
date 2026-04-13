using Npgsql;

namespace Citus.Accounting.Infrastructure.Persistence;

internal sealed class PostgresCommandScope : IAsyncDisposable
{
    private readonly bool _ownsConnection;

    private PostgresCommandScope(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        bool ownsConnection)
    {
        Connection = connection;
        Transaction = transaction;
        _ownsConnection = ownsConnection;
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
        if (_ownsConnection)
        {
            await Connection.DisposeAsync();
        }
    }
}
