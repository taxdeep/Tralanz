using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.BusinessAuth;

/// <summary>
/// PostgreSQL implementation of <see cref="IUserProfileOverrideStore"/>.
/// Schema is idempotent and applied at startup via EnsureSchemaAsync.
/// </summary>
public sealed class PostgreSqlUserProfileOverrideStore(PostgreSqlConnectionFactory connections) : IUserProfileOverrideStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS user_profile_overrides (
                user_id       UUID PRIMARY KEY,
                display_name  TEXT NULL,
                created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserProfileOverrideRecord?> GetByUserIdAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_id, display_name, created_at, updated_at
            FROM user_profile_overrides
            WHERE user_id = @user_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new UserProfileOverrideRecord(
            UserId: reader.GetGuid(0),
            DisplayName: reader.IsDBNull(1) ? null : reader.GetString(1),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(2),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(3));
    }

    public async Task<UserProfileOverrideRecord> UpsertDisplayNameAsync(
        UserId userId,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("display name is required", nameof(displayName));
        }

        var trimmed = displayName.Trim();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_profile_overrides (user_id, display_name, created_at, updated_at)
            VALUES (@user_id, @display_name, @now, @now)
            ON CONFLICT (user_id) DO UPDATE
            SET display_name = EXCLUDED.display_name,
                updated_at   = EXCLUDED.updated_at
            RETURNING user_id, display_name, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("display_name", trimmed);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // PostgreSQL guarantees RETURNING fires for INSERT ... ON CONFLICT
            // ... DO UPDATE; this should never trip.
            throw new InvalidOperationException("upsert returned no row");
        }

        return new UserProfileOverrideRecord(
            UserId: reader.GetGuid(0),
            DisplayName: reader.IsDBNull(1) ? null : reader.GetString(1),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(2),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(3));
    }
}
