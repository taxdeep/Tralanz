using Npgsql;

namespace Infrastructure.PostgreSQL;

public sealed class PostgreSqlConnectionFactory
{
    private readonly string _connectionString;

    public PostgreSqlConnectionFactory(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString))
            : connectionString;
    }

    /// <summary>
    /// Default connection-open path. Sets the Postgres GUC
    /// <c>app.bypass_company_filter = 'true'</c> immediately after
    /// open so the M13 row-level-security policies admit every row
    /// (matches pre-RLS behavior). Use <see cref="OpenWithCompanyAsync"/>
    /// for queries that want strict tenant isolation enforcement.
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await SetTenantBypassAsync(connection, cancellationToken);
        return connection;
    }

    /// <summary>
    /// M13: connection-open path that sets <c>app.company_id</c> so
    /// the RLS tenant_isolation policy filters rows by company.
    /// </summary>
    public async Task<NpgsqlConnection> OpenWithCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(companyId.Value))
        {
            throw new InvalidOperationException("OpenWithCompanyAsync requires a non-empty company id.");
        }

        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var setCommand = connection.CreateCommand();
        setCommand.CommandText = "select set_config('app.company_id', @company_id, false)";
        setCommand.Parameters.AddWithValue("company_id", companyId.Value);
        await setCommand.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task SetTenantBypassAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select set_config('app.bypass_company_filter', 'true', false)";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
