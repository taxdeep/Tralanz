using System.Threading;
using Npgsql;

namespace Citus.Accounting.Infrastructure.Persistence;

internal sealed record PostgresExecutionContext(
    NpgsqlConnection Connection,
    NpgsqlTransaction Transaction);

public sealed class PostgresExecutionContextAccessor
{
    private readonly AsyncLocal<PostgresExecutionContext?> _current = new();

    internal PostgresExecutionContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
