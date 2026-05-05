using Citus.Platform.Core.Abstractions;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PostgresPlatformAiProviderConfigStore : IPlatformAiProviderConfigStore
{
    private static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000002");

    private readonly PlatformPostgresConnectionFactory _connections;
    private readonly IPlatformSecretProtector _protector;

    public PostgresPlatformAiProviderConfigStore(
        PlatformPostgresConnectionFactory connections,
        IPlatformSecretProtector protector)
    {
        _connections = connections;
        _protector = protector;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists platform_ai_provider_config (
              id uuid primary key,
              provider text not null default 'disabled',
              base_url text,
              model text not null default '',
              max_tokens int not null default 1024,
              temperature numeric(4,2) not null default 0.7,
              api_key_protected text,
              updated_at timestamptz not null default now(),
              updated_by_user_id uuid,
              constraint platform_ai_provider_config_provider_chk
                check (provider in ('disabled','openai','anthropic','azure_openai')),
              constraint platform_ai_provider_config_max_tokens_chk
                check (max_tokens > 0 and max_tokens <= 200000),
              constraint platform_ai_provider_config_temperature_chk
                check (temperature >= 0 and temperature <= 2)
            );
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlatformAiProviderConfigSnapshot?> GetAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select provider, base_url, model, max_tokens, temperature,
                   api_key_protected, updated_at, updated_by_user_id
              from platform_ai_provider_config
             where id = @id;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", SingletonId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var apiKeyProtected = reader.IsDBNull(5) ? null : reader.GetString(5);

        return new PlatformAiProviderConfigSnapshot(
            Provider: reader.GetString(0),
            BaseUrl: reader.IsDBNull(1) ? null : reader.GetString(1),
            Model: reader.GetString(2),
            MaxTokens: reader.GetInt32(3),
            Temperature: (double)reader.GetDecimal(4),
            HasApiKey: !string.IsNullOrWhiteSpace(apiKeyProtected),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(6),
            UpdatedByUserId: reader.IsDBNull(7) ? null : reader.GetGuid(7));
    }

    public async Task<PlatformAiProviderConfigSnapshot> UpsertAsync(
        PlatformAiProviderConfigUpsertRequest request,
        UserId updatedByUserId,
        CancellationToken cancellationToken)
    {
        var existingApiKey = await GetRawApiKeyAsync(cancellationToken);

        string? apiKeyProtected;
        if (request.ClearApiKey)
        {
            apiKeyProtected = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.NewApiKey))
        {
            apiKeyProtected = _protector.Protect(request.NewApiKey!);
        }
        else
        {
            apiKeyProtected = existingApiKey;
        }

        var normalizedProvider = string.IsNullOrWhiteSpace(request.Provider)
            ? PlatformAiProviderKeys.Disabled
            : request.Provider.Trim().ToLowerInvariant();
        if (!PlatformAiProviderKeys.IsValid(normalizedProvider))
        {
            throw new InvalidOperationException(
                $"Unsupported AI provider '{normalizedProvider}'. Expected one of: " +
                string.Join(", ", PlatformAiProviderKeys.All));
        }

        const string sql = """
            insert into platform_ai_provider_config
              (id, provider, base_url, model, max_tokens, temperature,
               api_key_protected, updated_at, updated_by_user_id)
            values
              (@id, @provider, @base_url, @model, @max_tokens, @temperature,
               @api_key_protected, now(), @updated_by_user_id)
            on conflict (id) do update set
              provider = excluded.provider,
              base_url = excluded.base_url,
              model = excluded.model,
              max_tokens = excluded.max_tokens,
              temperature = excluded.temperature,
              api_key_protected = excluded.api_key_protected,
              updated_at = now(),
              updated_by_user_id = excluded.updated_by_user_id
            returning updated_at;
            """;

        var clampedMaxTokens = Math.Clamp(request.MaxTokens, 1, 200_000);
        var clampedTemperature = (decimal)Math.Clamp(request.Temperature, 0, 2);
        var trimmedBaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? null : request.BaseUrl!.Trim();

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", SingletonId);
        command.Parameters.AddWithValue("provider", normalizedProvider);
        command.Parameters.AddWithValue("base_url", (object?)trimmedBaseUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("model", request.Model?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("max_tokens", clampedMaxTokens);
        command.Parameters.AddWithValue("temperature", clampedTemperature);
        command.Parameters.AddWithValue("api_key_protected", (object?)apiKeyProtected ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_by_user_id", updatedByUserId);

        // Npgsql 6+ reads timestamptz as DateTime (UTC kind) by default,
        // not DateTimeOffset — the direct cast on the boxed scalar
        // throws "Unable to cast object of type 'System.DateTime' to
        // type 'System.DateTimeOffset'". Read DateTime first, then
        // wrap in a zero-offset DateTimeOffset since the column is
        // already UTC.
        var rawUpdatedAt = await command.ExecuteScalarAsync(cancellationToken);
        var updatedAt = rawUpdatedAt switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("updated_at returned an unexpected scalar type."),
        };

        return new PlatformAiProviderConfigSnapshot(
            Provider: normalizedProvider,
            BaseUrl: trimmedBaseUrl,
            Model: request.Model?.Trim() ?? string.Empty,
            MaxTokens: clampedMaxTokens,
            Temperature: (double)clampedTemperature,
            HasApiKey: !string.IsNullOrWhiteSpace(apiKeyProtected),
            UpdatedAt: updatedAt,
            UpdatedByUserId: updatedByUserId);
    }

    public async Task<string?> GetRawApiKeyAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select api_key_protected
              from platform_ai_provider_config
             where id = @id;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", SingletonId);

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        return raw is null or DBNull ? null : (string)raw;
    }
}
