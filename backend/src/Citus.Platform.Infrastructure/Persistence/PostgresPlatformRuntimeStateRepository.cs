using System.Text.Json;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PostgresPlatformRuntimeStateRepository(
    PlatformPostgresConnectionFactory connectionFactory) : IPlatformRuntimeStateRepository
{
    private const string MaintenanceStateKey = "maintenance";
    private const string NotificationReadinessStateKey = "notification_readiness";
    private const string FirstCompanySetupStateKey = "first_company_setup";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists platform_runtime_state (
              state_key text primary key,
              json jsonb not null,
              updated_at timestamptz not null default now()
            );
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlatformMaintenanceState?> GetMaintenanceStateAsync(CancellationToken cancellationToken)
    {
        return await ReadStateAsync<PlatformMaintenanceState>(MaintenanceStateKey, cancellationToken);
    }

    public async Task<PlatformMaintenanceState> UpsertMaintenanceStateAsync(
        PlatformMaintenanceState state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into platform_runtime_state (
              state_key,
              json,
              updated_at
            )
            values (
              @state_key,
              cast(@json as jsonb),
              now()
            )
            on conflict (state_key) do update
            set json = excluded.json,
                updated_at = now();
            """;

        var normalizedState = state with
        {
            Message = string.IsNullOrWhiteSpace(state.Message)
                ? "Maintenance state updated by platform control."
                : state.Message.Trim(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("state_key", MaintenanceStateKey);
        command.Parameters.AddWithValue("json", Serialize(normalizedState));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return normalizedState;
    }

    public async Task<PlatformNotificationReadinessState?> GetNotificationReadinessStateAsync(CancellationToken cancellationToken)
    {
        return await ReadStateAsync<PlatformNotificationReadinessState>(NotificationReadinessStateKey, cancellationToken);
    }

    public async Task<PlatformNotificationReadinessState> UpsertNotificationReadinessStateAsync(
        PlatformNotificationReadinessState state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into platform_runtime_state (
              state_key,
              json,
              updated_at
            )
            values (
              @state_key,
              cast(@json as jsonb),
              now()
            )
            on conflict (state_key) do update
            set json = excluded.json,
                updated_at = now();
            """;

        var normalizedState = state with
        {
            TestStatus = NormalizeTestStatus(state.TestStatus),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("state_key", NotificationReadinessStateKey);
        command.Parameters.AddWithValue("json", Serialize(normalizedState));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return normalizedState;
    }

    public async Task<PlatformFirstCompanySetupState?> GetFirstCompanySetupStateAsync(CancellationToken cancellationToken)
    {
        return await ReadStateAsync<PlatformFirstCompanySetupState>(FirstCompanySetupStateKey, cancellationToken);
    }

    public async Task<PlatformFirstCompanySetupState> UpsertFirstCompanySetupStateAsync(
        PlatformFirstCompanySetupState state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into platform_runtime_state (
              state_key,
              json,
              updated_at
            )
            values (
              @state_key,
              cast(@json as jsonb),
              now()
            )
            on conflict (state_key) do update
            set json = excluded.json,
                updated_at = now();
            """;

        var normalizedDecisionStatus = NormalizeFirstCompanyDecisionStatus(state.DecisionStatus);
        var normalizedState = state with
        {
            DecisionStatus = normalizedDecisionStatus,
            DeferredAtUtc = string.Equals(
                normalizedDecisionStatus,
                PlatformFirstCompanySetupState.DeferredDecisionStatus,
                StringComparison.Ordinal)
                ? state.DeferredAtUtc ?? DateTimeOffset.UtcNow
                : null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("state_key", FirstCompanySetupStateKey);
        command.Parameters.AddWithValue("json", Serialize(normalizedState));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return normalizedState;
    }

    private async Task<T?> ReadStateAsync<T>(string stateKey, CancellationToken cancellationToken)
    {
        const string sql = """
            select json
            from platform_runtime_state
            where state_key = @state_key
            limit 1;
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("state_key", stateKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return default;
        }

        return Deserialize<T>(reader.GetString(0));
    }

    private static string NormalizeTestStatus(string testStatus)
    {
        if (string.IsNullOrWhiteSpace(testStatus))
        {
            return "untested";
        }

        return testStatus.Trim().ToLowerInvariant() switch
        {
            "passed" => "passed",
            "failed" => "failed",
            _ => "untested"
        };
    }

    private static string NormalizeFirstCompanyDecisionStatus(string decisionStatus)
    {
        if (string.IsNullOrWhiteSpace(decisionStatus))
        {
            return PlatformFirstCompanySetupState.PendingDecisionStatus;
        }

        return decisionStatus.Trim().ToLowerInvariant() switch
        {
            PlatformFirstCompanySetupState.DeferredDecisionStatus =>
                PlatformFirstCompanySetupState.DeferredDecisionStatus,
            _ => PlatformFirstCompanySetupState.PendingDecisionStatus
        };
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, JsonOptions) ??
        throw new InvalidOperationException($"Unable to deserialize {typeof(T).Name} from platform runtime storage.");
}
