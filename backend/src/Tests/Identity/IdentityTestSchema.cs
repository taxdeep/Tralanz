using Npgsql;

namespace Tests.Identity;

internal static class IdentityTestSchema
{
    public static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    public static string BuildSchemaConnectionString(string baseConnectionString, string schemaName)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schemaName
        };
        return builder.ConnectionString;
    }

    public static async Task CreateSchemaAsync(string connectionString, string schemaName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"create schema {schemaName};";
        await command.ExecuteNonQueryAsync();
    }

    public static async Task EnsurePlatformUserIdSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
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
        await command.ExecuteNonQueryAsync();
    }

    public static async Task EnsurePlatformCompanyIdSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
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
        await command.ExecuteNonQueryAsync();
    }

    public static async Task EnsureEntityNumberSequenceTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
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
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DropSchemaAsync(string connectionString, string schemaName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"drop schema if exists {schemaName} cascade;";
        await command.ExecuteNonQueryAsync();
    }

    public static string NewSchemaName() => $"id_test_{Guid.NewGuid():N}";
}
