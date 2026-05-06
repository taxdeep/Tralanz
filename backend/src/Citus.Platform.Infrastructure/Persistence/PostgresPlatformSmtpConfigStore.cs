using Citus.Platform.Core.Abstractions;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PostgresPlatformSmtpConfigStore : IPlatformSmtpConfigStore
{
    /// <summary>Fixed sentinel id — singleton row pattern. Every read
    /// and write addresses this id explicitly so a stray second row
    /// inserted via raw SQL doesn't change runtime behaviour.</summary>
    private static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    private readonly PlatformPostgresConnectionFactory _connections;
    private readonly IPlatformSecretProtector _protector;

    public PostgresPlatformSmtpConfigStore(
        PlatformPostgresConnectionFactory connections,
        IPlatformSecretProtector protector)
    {
        _connections = connections;
        _protector = protector;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists platform_smtp_config (
              id uuid primary key,
              provider text not null default 'disabled',
              from_email text not null default '',
              from_display_name text not null default 'Tralanz Books',
              host text not null default '',
              port int not null default 587,
              use_ssl boolean not null default true,
              username text not null default '',
              password_protected text,
              updated_at timestamptz not null default now(),
              updated_by_user_id uuid,
              constraint platform_smtp_config_provider_chk
                check (provider in ('disabled','smtp'))
            );
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlatformSmtpConfigSnapshot?> GetAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select provider, from_email, from_display_name, host, port,
                   use_ssl, username, password_protected, updated_at,
                   updated_by_user_id
              from platform_smtp_config
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

        var passwordProtected = reader.IsDBNull(7) ? null : reader.GetString(7);

        return new PlatformSmtpConfigSnapshot(
            Provider: reader.GetString(0),
            FromEmail: reader.GetString(1),
            FromDisplayName: reader.GetString(2),
            Host: reader.GetString(3),
            Port: reader.GetInt32(4),
            UseSsl: reader.GetBoolean(5),
            Username: reader.GetString(6),
            HasPassword: !string.IsNullOrWhiteSpace(passwordProtected),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(8),
            UpdatedByUserId: reader.IsDBNull(9) ? null : UserId.Parse(reader.GetString(9)));
    }

    public async Task<PlatformSmtpConfigSnapshot> UpsertAsync(
        PlatformSmtpConfigUpsertRequest request,
        UserId updatedByUserId,
        CancellationToken cancellationToken)
    {
        // Resolve the password column we want to persist: explicit clear
        // wins, then "new password supplied" rewrites the envelope, then
        // the fallback preserves whatever's already in the row.
        string? newPasswordProtected;
        var existing = await GetRawPasswordAsync(cancellationToken);
        if (request.ClearPassword)
        {
            newPasswordProtected = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            newPasswordProtected = _protector.Protect(request.NewPassword!);
        }
        else
        {
            newPasswordProtected = existing;
        }

        const string sql = """
            insert into platform_smtp_config
              (id, provider, from_email, from_display_name, host, port,
               use_ssl, username, password_protected, updated_at,
               updated_by_user_id)
            values
              (@id, @provider, @from_email, @from_display_name, @host, @port,
               @use_ssl, @username, @password_protected, now(),
               @updated_by_user_id)
            on conflict (id) do update set
              provider = excluded.provider,
              from_email = excluded.from_email,
              from_display_name = excluded.from_display_name,
              host = excluded.host,
              port = excluded.port,
              use_ssl = excluded.use_ssl,
              username = excluded.username,
              password_protected = excluded.password_protected,
              updated_at = now(),
              updated_by_user_id = excluded.updated_by_user_id
            returning updated_at;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", SingletonId);
        command.Parameters.AddWithValue("provider", request.Provider.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("from_email", request.FromEmail?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("from_display_name",
            string.IsNullOrWhiteSpace(request.FromDisplayName) ? "Tralanz Books" : request.FromDisplayName.Trim());
        command.Parameters.AddWithValue("host", request.Host?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("port", request.Port);
        command.Parameters.AddWithValue("use_ssl", request.UseSsl);
        command.Parameters.AddWithValue("username", request.Username?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("password_protected",
            (object?)newPasswordProtected ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_by_user_id", updatedByUserId);

        // Npgsql 6+ reads timestamptz as DateTime (UTC kind) by default,
        // not DateTimeOffset — the direct cast on the boxed scalar
        // throws "Unable to cast object of type 'System.DateTime' to
        // type 'System.DateTimeOffset'". Wrap in a zero-offset
        // DateTimeOffset since the column is already UTC.
        var rawUpdatedAt = await command.ExecuteScalarAsync(cancellationToken);
        var updatedAt = rawUpdatedAt switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("updated_at returned an unexpected scalar type."),
        };

        return new PlatformSmtpConfigSnapshot(
            Provider: request.Provider.Trim().ToLowerInvariant(),
            FromEmail: request.FromEmail?.Trim() ?? string.Empty,
            FromDisplayName: string.IsNullOrWhiteSpace(request.FromDisplayName)
                ? "Tralanz Books"
                : request.FromDisplayName.Trim(),
            Host: request.Host?.Trim() ?? string.Empty,
            Port: request.Port,
            UseSsl: request.UseSsl,
            Username: request.Username?.Trim() ?? string.Empty,
            HasPassword: !string.IsNullOrWhiteSpace(newPasswordProtected),
            UpdatedAt: updatedAt,
            UpdatedByUserId: updatedByUserId);
    }

    /// <summary>
    /// Reads only the encrypted password column so UpsertAsync can
    /// preserve it when the operator submits a form without entering a
    /// new password. Stays in this class so the protector key never
    /// leaves it.
    /// </summary>
    internal async Task<string?> GetRawPasswordAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select password_protected
              from platform_smtp_config
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
