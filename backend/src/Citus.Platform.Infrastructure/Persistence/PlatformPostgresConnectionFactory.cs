using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PlatformPostgresConnectionFactory(string connectionString)
{
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
