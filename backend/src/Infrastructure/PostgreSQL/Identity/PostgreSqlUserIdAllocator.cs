using Npgsql;
using SharedKernel.Identity;

namespace Infrastructure.PostgreSQL.Identity;

public sealed class PostgreSqlUserIdAllocator : IUserIdAllocator
{
    public async Task<UserId> AllocateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await PostgreSqlIdentitySchemaBootstrap.EnsureUserIdSequenceAsync(connection, transaction, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update platform_user_id_sequence
            set next_ordinal = next_ordinal + 1
            where singleton_key = true
            returning next_ordinal - 1 as ordinal;
            """;

        var ordinal = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return UserId.FromOrdinal(ordinal);
    }
}
