using Modules.CompanyAccess.SystemSetup;
using Npgsql;
using SharedKernel.CompanyAccess;

namespace Infrastructure.PostgreSQL.CompanyAccess;

public sealed class PostgreSqlSystemSetupStore : ISystemSetupStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlSystemSetupStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<SystemSetupPreference> GetAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select number_display_mode, updated_at
            from user_preferences
            where user_id = @user_id
            limit 1;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SystemSetupPreference(userId, NumberDisplayModeDefaults.Default, DateTimeOffset.UtcNow);
        }

        var storedCode = reader.GetString(reader.GetOrdinal("number_display_mode"));
        NumberDisplayModeDefaults.TryParseCode(storedCode, out var mode);
        return new SystemSetupPreference(
            userId,
            mode,
            CoerceTimestamp(reader.GetValue(reader.GetOrdinal("updated_at"))));
    }

    public async Task<SystemSetupPreference> SaveAsync(
        UserId userId,
        NumberDisplayMode numberDisplayMode,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into user_preferences (
              user_id,
              number_display_mode,
              created_at,
              updated_at
            )
            values (
              @user_id,
              @number_display_mode,
              now(),
              now()
            )
            on conflict (user_id)
            do update
              set number_display_mode = excluded.number_display_mode,
                  updated_at = now()
            returning updated_at;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("number_display_mode", NumberDisplayModeDefaults.ToCode(numberDisplayMode));

        var updatedAt = CoerceTimestamp(await command.ExecuteScalarAsync(cancellationToken));

        return new SystemSetupPreference(userId, numberDisplayMode, updatedAt);
    }

    private static DateTimeOffset CoerceTimestamp(object? value)
    {
        if (value is DateTimeOffset offset)
        {
            return offset;
        }

        if (value is DateTime dateTime)
        {
            var normalized = dateTime.Kind == DateTimeKind.Utc
                ? dateTime
                : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
            return new DateTimeOffset(normalized);
        }

        return DateTimeOffset.UtcNow;
    }
}
