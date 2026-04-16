using System.Security.Cryptography;
using System.Text;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Platform.Core.Runtime;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PostgresPlatformBusinessSessionRepository(
    PlatformPostgresConnectionFactory connectionFactory,
    SysAdminPasswordHasher passwordHasher) : IPlatformBusinessSessionRepository
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create extension if not exists pgcrypto;

            alter table users
              add column if not exists status text not null default 'active';

            alter table users
              add column if not exists locked_until timestamptz;

            create table if not exists business_sessions (
              id uuid primary key default gen_random_uuid(),
              token_hash text not null unique,
              user_id uuid not null references users(id) on delete cascade,
              active_company_id uuid not null references companies(id) on delete restrict,
              membership_id uuid not null references company_memberships(id) on delete cascade,
              role text not null,
              permissions jsonb not null default '[]'::jsonb,
              company_status text not null,
              permission_version text,
              expires_at timestamptz not null,
              created_at timestamptz not null default now(),
              constraint business_sessions_role_chk check (role in ('owner', 'user')),
              constraint business_sessions_permissions_array_chk check (jsonb_typeof(permissions) = 'array'),
              constraint business_sessions_company_status_chk check (company_status in ('active', 'inactive', 'suspended', 'archived'))
            );

            create index if not exists idx_business_sessions_user_company_expiry
              on business_sessions (user_id, active_company_id, expires_at desc);

            create index if not exists idx_business_sessions_token_expiry
              on business_sessions (token_hash, expires_at desc);
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlatformBusinessSessionResult> AuthenticateAsync(
        string login,
        string password,
        TimeSpan sessionLifetime,
        string? remoteIp,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            return Failed("invalid_credentials", "Email/username and password are required.");
        }

        await EnsureSchemaAsync(cancellationToken);

        var normalizedLogin = login.Trim().ToLowerInvariant();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var account = await ReadAccountByLoginAsync(connection, transaction, normalizedLogin, cancellationToken);
        if (account is null || !passwordHasher.VerifyPassword(password, account.PasswordHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("invalid_credentials", "The email/username or password is invalid.");
        }

        if (!string.Equals(account.Status, "active", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("account_not_active", $"Platform account is {account.Status}.");
        }

        if (account.LockedUntilUtc.HasValue && account.LockedUntilUtc.Value > DateTimeOffset.UtcNow)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("account_locked", "Platform account is locked.");
        }

        var preferredCompanyId = await ReadPreferredActiveCompanyIdAsync(
            connection,
            transaction,
            account.Id,
            cancellationToken);
        var membership = await ResolveMembershipContextAsync(
            connection,
            transaction,
            account.Id,
            preferredCompanyId,
            cancellationToken);
        if (membership is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("no_company_access", "This platform account does not currently have access to an active business company.");
        }

        var sessionToken = CreateSessionToken();
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(sessionLifetime);

        await InsertSessionAsync(
            connection,
            transaction,
            HashSessionToken(sessionToken),
            account.Id,
            membership,
            expiresAtUtc,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new PlatformBusinessSessionResult
        {
            Succeeded = true,
            SessionToken = sessionToken,
            UserId = account.Id,
            ActiveCompanyId = membership.CompanyId,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public async Task<PlatformBusinessSessionResult> ValidateSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return Failed("missing_session", "Business session token is required.");
        }

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var session = await ReadSessionAsync(connection, transaction, HashSessionToken(sessionToken.Trim()), cancellationToken);
        if (session is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("invalid_session", "Business session was not found.");
        }

        if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            await DeleteSessionAsync(connection, transaction, session.SessionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Failed("expired_session", "Business session has expired.");
        }

        if (!string.Equals(session.AccountStatus, "active", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("account_not_active", $"Platform account is {session.AccountStatus}.");
        }

        if (session.LockedUntilUtc.HasValue && session.LockedUntilUtc.Value > DateTimeOffset.UtcNow)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("account_locked", "Platform account is locked.");
        }

        var membership = await ResolveMembershipContextAsync(
            connection,
            transaction,
            session.UserId,
            session.ActiveCompanyId,
            cancellationToken);
        if (membership is null)
        {
            await DeleteSessionAsync(connection, transaction, session.SessionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Failed("company_access_unavailable", "Business session company access is no longer available.");
        }

        await UpdateSessionContextAsync(connection, transaction, session.SessionId, membership, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PlatformBusinessSessionResult
        {
            Succeeded = true,
            UserId = session.UserId,
            ActiveCompanyId = membership.CompanyId,
            ExpiresAtUtc = session.ExpiresAtUtc
        };
    }

    public async Task<PlatformBusinessSessionResult> SwitchActiveCompanyAsync(
        string sessionToken,
        Guid activeCompanyId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return Failed("missing_session", "Business session token is required.");
        }

        if (activeCompanyId == Guid.Empty)
        {
            return Failed("invalid_company", "Active company id is required.");
        }

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var session = await ReadSessionAsync(connection, transaction, HashSessionToken(sessionToken.Trim()), cancellationToken);
        if (session is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("invalid_session", "Business session was not found.");
        }

        if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            await DeleteSessionAsync(connection, transaction, session.SessionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Failed("expired_session", "Business session has expired.");
        }

        var membership = await ResolveMembershipContextAsync(
            connection,
            transaction,
            session.UserId,
            activeCompanyId,
            cancellationToken);
        if (membership is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("company_not_available", "The requested company is not available in this business session.");
        }

        await UpdateSessionContextAsync(connection, transaction, session.SessionId, membership, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PlatformBusinessSessionResult
        {
            Succeeded = true,
            UserId = session.UserId,
            ActiveCompanyId = membership.CompanyId,
            ExpiresAtUtc = session.ExpiresAtUtc
        };
    }

    public async Task RevokeSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from business_sessions
            where token_hash = @token_hash;
            """;
        command.Parameters.AddWithValue("token_hash", HashSessionToken(sessionToken.Trim()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PlatformBusinessSessionResult Failed(string code, string message) =>
        new()
        {
            Succeeded = false,
            FailureCode = code,
            FailureMessage = message
        };

    private static string CreateSessionToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private static string HashSessionToken(string sessionToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken));
        return Convert.ToHexString(bytes);
    }

    private static async Task<AccountRecord?> ReadAccountByLoginAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string login,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id,
                   email,
                   username,
                   password_hash,
                   status,
                   locked_until
            from users
            where lower(email) = @login
               or lower(coalesce(username, '')) = @login
            order by case when lower(email) = @login then 0 else 1 end
            limit 1;
            """;
        command.Parameters.AddWithValue("login", login);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AccountRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("username"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("username")).Trim(),
            reader.GetString(reader.GetOrdinal("password_hash")).Trim(),
            reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant(),
            reader.IsDBNull(reader.GetOrdinal("locked_until"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("locked_until")));
    }

    private static async Task<Guid?> ReadPreferredActiveCompanyIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select active_company_id
            from business_sessions
            where user_id = @user_id
            order by created_at desc
            limit 1;
            """;
        command.Parameters.AddWithValue("user_id", userId);

        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            Guid value => value,
            _ => null
        };
    }

    private static async Task<MembershipContextRecord?> ResolveMembershipContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        Guid? preferredCompanyId,
        CancellationToken cancellationToken)
    {
        if (preferredCompanyId.HasValue && preferredCompanyId.Value != Guid.Empty)
        {
            var preferred = await ReadMembershipContextAsync(
                connection,
                transaction,
                userId,
                preferredCompanyId.Value,
                cancellationToken);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select m.id as membership_id,
                   m.company_id,
                   m.role,
                   m.permissions::text as permissions_json,
                   c.status as company_status
            from company_memberships m
            inner join companies c on c.id = m.company_id
            where m.user_id = @user_id
              and m.is_active = true
              and c.status in ('active', 'inactive')
            order by case when c.status = 'active' then 0 else 1 end,
                     c.entity_number,
                     c.legal_name
            limit 1;
            """;
        command.Parameters.AddWithValue("user_id", userId);

        return await ReadMembershipContextAsync(command, cancellationToken);
    }

    private static async Task<MembershipContextRecord?> ReadMembershipContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select m.id as membership_id,
                   m.company_id,
                   m.role,
                   m.permissions::text as permissions_json,
                   c.status as company_status
            from company_memberships m
            inner join companies c on c.id = m.company_id
            where m.user_id = @user_id
              and m.company_id = @company_id
              and m.is_active = true
              and c.status in ('active', 'inactive')
            limit 1;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("company_id", companyId);

        return await ReadMembershipContextAsync(command, cancellationToken);
    }

    private static async Task<MembershipContextRecord?> ReadMembershipContextAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MembershipContextRecord(
            reader.GetGuid(reader.GetOrdinal("membership_id")),
            reader.GetGuid(reader.GetOrdinal("company_id")),
            reader.GetString(reader.GetOrdinal("role")).Trim().ToLowerInvariant(),
            reader.IsDBNull(reader.GetOrdinal("permissions_json"))
                ? "[]"
                : reader.GetString(reader.GetOrdinal("permissions_json")).Trim(),
            reader.GetString(reader.GetOrdinal("company_status")).Trim().ToLowerInvariant());
    }

    private static async Task InsertSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tokenHash,
        Guid userId,
        MembershipContextRecord membership,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into business_sessions (
              token_hash,
              user_id,
              active_company_id,
              membership_id,
              role,
              permissions,
              company_status,
              permission_version,
              expires_at
            )
            values (
              @token_hash,
              @user_id,
              @active_company_id,
              @membership_id,
              @role,
              @permissions::jsonb,
              @company_status,
              null,
              @expires_at
            );
            """;
        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("active_company_id", membership.CompanyId);
        command.Parameters.AddWithValue("membership_id", membership.MembershipId);
        command.Parameters.AddWithValue("role", membership.Role);
        command.Parameters.AddWithValue("permissions", membership.PermissionsJson);
        command.Parameters.AddWithValue("company_status", membership.CompanyStatus);
        command.Parameters.AddWithValue("expires_at", expiresAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateSessionContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        MembershipContextRecord membership,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update business_sessions
            set active_company_id = @active_company_id,
                membership_id = @membership_id,
                role = @role,
                permissions = @permissions::jsonb,
                company_status = @company_status
            where id = @session_id;
            """;
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("active_company_id", membership.CompanyId);
        command.Parameters.AddWithValue("membership_id", membership.MembershipId);
        command.Parameters.AddWithValue("role", membership.Role);
        command.Parameters.AddWithValue("permissions", membership.PermissionsJson);
        command.Parameters.AddWithValue("company_status", membership.CompanyStatus);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            delete from business_sessions
            where id = @session_id;
            """;
        command.Parameters.AddWithValue("session_id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SessionRecord?> ReadSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select s.id,
                   s.user_id,
                   s.active_company_id,
                   s.expires_at,
                   u.status as account_status,
                   u.locked_until
            from business_sessions s
            inner join users u on u.id = s.user_id
            where s.token_hash = @token_hash
            limit 1;
            """;
        command.Parameters.AddWithValue("token_hash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SessionRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("user_id")),
            reader.GetGuid(reader.GetOrdinal("active_company_id")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("expires_at")),
            reader.GetString(reader.GetOrdinal("account_status")).Trim().ToLowerInvariant(),
            reader.IsDBNull(reader.GetOrdinal("locked_until"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("locked_until")));
    }

    private sealed record AccountRecord(
        Guid Id,
        string Email,
        string Username,
        string PasswordHash,
        string Status,
        DateTimeOffset? LockedUntilUtc);

    private sealed record MembershipContextRecord(
        Guid MembershipId,
        Guid CompanyId,
        string Role,
        string PermissionsJson,
        string CompanyStatus);

    private sealed record SessionRecord(
        Guid SessionId,
        Guid UserId,
        Guid ActiveCompanyId,
        DateTimeOffset ExpiresAtUtc,
        string AccountStatus,
        DateTimeOffset? LockedUntilUtc);
}
