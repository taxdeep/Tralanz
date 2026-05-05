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
