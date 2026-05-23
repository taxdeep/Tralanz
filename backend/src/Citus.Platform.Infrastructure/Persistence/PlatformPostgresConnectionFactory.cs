using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PlatformPostgresConnectionFactory(string connectionString)
{
    /// <summary>
    /// Default connection-open path for the platform layer
    /// (sessions, MFA, platform admin, etc). Sets the Postgres GUC
    /// <c>app.bypass_company_filter = 'true'</c> immediately after
    /// open so the M13 row-level-security policies admit every row
    /// the platform layer needs to inspect.
    ///
    /// Why this matters: platform-layer repositories operate ACROSS
    /// tenants (e.g. <c>PostgresPlatformBusinessSessionRepository.ValidateSessionAsync</c>
    /// reads <c>business_sessions</c> → <c>company_memberships</c> →
    /// <c>companies</c> for whichever tenant the session belongs to).
    /// None of those queries set <c>app.company_id</c>, so without
    /// bypass mode every read returns zero rows post-M13 FORCE — which
    /// silently deletes valid sessions because the validator
    /// interprets "membership not found" as "session lost company
    /// access".
    ///
    /// PR #66 added the same bypass to <c>PostgresConnectionFactory</c>
    /// (accounting layer) and <c>PostgreSqlConnectionFactory</c>
    /// (inventory layer) but missed this platform factory; the
    /// omission only surfaced on the live test server when synthetic
    /// session rows started disappearing on first validate.
    /// </summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SetTenantBypassAsync(connection, cancellationToken);
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
