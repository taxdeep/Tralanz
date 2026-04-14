using System.Text.Json;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Metadata;
using Citus.Platform.Core.Modules;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PostgresPlatformMetadataRepository(
    PlatformPostgresConnectionFactory connectionFactory) : IPlatformMetadataRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists platform_modules (
              id uuid primary key,
              module_key text not null unique,
              json jsonb not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create table if not exists platform_entities (
              id uuid primary key,
              entity_name text not null unique,
              module_key text not null,
              storage_table text not null,
              json jsonb not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create index if not exists idx_platform_entities_module_key
              on platform_entities (module_key);

            create index if not exists idx_platform_entities_storage_table
              on platform_entities (storage_table);
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertModuleAsync(PlatformModuleManifest moduleManifest, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into platform_modules (
              id,
              module_key,
              json,
              created_at,
              updated_at
            )
            values (
              @id,
              @module_key,
              cast(@json as jsonb),
              now(),
              now()
            )
            on conflict (module_key) do update
            set json = excluded.json,
                updated_at = now();
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", moduleManifest.Id);
        command.Parameters.AddWithValue("module_key", moduleManifest.Key);
        command.Parameters.AddWithValue("json", Serialize(moduleManifest));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlatformModuleManifest>> ListModulesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select json
            from platform_modules
            order by module_key;
            """;

        return await ReadListAsync(
            sql,
            reader => Deserialize<PlatformModuleManifest>(reader.GetString(0)),
            cancellationToken);
    }

    public async Task UpsertEntityAsync(CoreEntityDefinition entityDefinition, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into platform_entities (
              id,
              entity_name,
              module_key,
              storage_table,
              json,
              created_at,
              updated_at
            )
            values (
              @id,
              @entity_name,
              @module_key,
              @storage_table,
              cast(@json as jsonb),
              now(),
              now()
            )
            on conflict (entity_name) do update
            set module_key = excluded.module_key,
                storage_table = excluded.storage_table,
                json = excluded.json,
                updated_at = now();
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", entityDefinition.Id);
        command.Parameters.AddWithValue("entity_name", entityDefinition.Name);
        command.Parameters.AddWithValue("module_key", entityDefinition.ModuleKey);
        command.Parameters.AddWithValue("storage_table", entityDefinition.StorageTable);
        command.Parameters.AddWithValue("json", Serialize(entityDefinition));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CoreEntityDefinition?> GetEntityAsync(string entityName, CancellationToken cancellationToken)
    {
        const string sql = """
            select json
            from platform_entities
            where entity_name = @entity_name
            limit 1;
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("entity_name", entityName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return Deserialize<CoreEntityDefinition>(reader.GetString(0));
    }

    public async Task<IReadOnlyList<CoreEntityDefinition>> ListEntitiesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select json
            from platform_entities
            order by module_key, entity_name;
            """;

        return await ReadListAsync(
            sql,
            reader => Deserialize<CoreEntityDefinition>(reader.GetString(0)),
            cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(
        string sql,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var results = new List<T>();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(map(reader));
        }

        return results;
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, JsonOptions) ??
        throw new InvalidOperationException($"Unable to deserialize {typeof(T).Name} from platform metadata storage.");
}
