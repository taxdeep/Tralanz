using Npgsql;

namespace Infrastructure.PostgreSQL.Identity;

internal static class PostgreSqlIdentitySchemaBootstrap
{
    public static async Task EnsureUserIdSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            create table if not exists platform_user_id_sequence (
              singleton_key boolean primary key default true,
              next_ordinal bigint not null,
              check (singleton_key = true)
            );
            insert into platform_user_id_sequence (singleton_key, next_ordinal)
            values (true, 1)
            on conflict (singleton_key) do nothing;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task EnsureCompanyIdSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            create table if not exists platform_company_id_sequence (
              singleton_key boolean primary key default true,
              next_ordinal bigint not null,
              check (singleton_key = true)
            );
            insert into platform_company_id_sequence (singleton_key, next_ordinal)
            values (true, 1)
            on conflict (singleton_key) do nothing;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task EnsureEntityNumberSequenceTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            create table if not exists company_entity_number_sequences (
              company_id char(7) not null,
              entity_year integer not null,
              next_ordinal bigint not null,
              primary key (company_id, entity_year)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
