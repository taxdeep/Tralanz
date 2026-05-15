using Npgsql;

namespace Citus.Accounting.Infrastructure.Persistence;

internal static class PostgresSchemaChecks
{
    public static async Task EnsureTableColumnsAsync(
        PostgresConnectionFactory connections,
        string tableName,
        IReadOnlyCollection<string> columnNames,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTableColumnsAsync(connection, tableName, columnNames, failureMessage, cancellationToken).ConfigureAwait(false);
    }

    public static async Task EnsureTableColumnsAsync(
        NpgsqlConnection connection,
        string tableName,
        IReadOnlyCollection<string> columnNames,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::integer
            from information_schema.columns
            where table_schema = current_schema()
              and table_name = @table_name
              and column_name = any(@column_names);
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("column_names", columnNames.ToArray());

        var matchedColumns = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
        if (matchedColumns != columnNames.Count)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }
}
