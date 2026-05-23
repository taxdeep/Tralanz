using Npgsql;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresConnectionFactory
{
    public PostgresConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    /// <summary>
    /// Default connection-open path. Sets the Postgres GUC
    /// <c>app.bypass_company_filter = 'true'</c> immediately after
    /// open so the M13 row-level-security policies admit every row
    /// (matches pre-RLS behavior). Use <see cref="OpenWithCompanyAsync"/>
    /// when the caller has a known company context and wants RLS to
    /// actively enforce tenant isolation.
    ///
    /// Npgsql returns the connection to the pool on Dispose, running
    /// DISCARD ALL by default which clears these GUCs — no
    /// cross-request leak.
    /// </summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await SetTenantBypassAsync(connection, cancellationToken);
        return connection;
    }

    /// <summary>
    /// M13: connection-open path that sets the Postgres GUC
    /// <c>app.company_id</c> so the tenant_isolation RLS policy
    /// admits only rows for the given company. A query that
    /// (incorrectly) omits the <c>company_id = @company_id</c>
    /// predicate still returns zero foreign-tenant rows.
    /// </summary>
    public async Task<NpgsqlConnection> OpenWithCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(companyId.Value))
        {
            throw new InvalidOperationException("OpenWithCompanyAsync requires a non-empty company id.");
        }

        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var setCommand = connection.CreateCommand())
        {
            // Use a parameter so we never have to worry about
            // quoting; format(SET app.company_id = '...') would
            // need escaping. SET takes a literal value, so we
            // interpolate via the parameter binding helper.
            setCommand.CommandText = "select set_config('app.company_id', @company_id, false)";
            setCommand.Parameters.AddWithValue("company_id", companyId.Value);
            await setCommand.ExecuteNonQueryAsync(cancellationToken);
        }
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
