using System.Security.Cryptography;
using System.Text;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed self-serve password reset for Business accounts.
/// See <see cref="IPlatformBusinessPasswordResetService"/> for the
/// flow contract. Tokens are 32-byte random URL-safe strings;
/// only their SHA-256 hash is persisted, so a DB read does not leak
/// usable tokens. Re-issuing a token before redemption invalidates
/// the previous one (the per-account index keeps at most one active
/// row).
/// </summary>
public sealed class PostgresPlatformBusinessPasswordResetService : IPlatformBusinessPasswordResetService
{
    private const int TokenByteLength = 32;
    private const int TokenLifetimeMinutes = 15;
    private const int MinPasswordLength = 8;

    private readonly PlatformPostgresConnectionFactory _connections;
    private readonly SysAdminPasswordHasher _passwordHasher;

    public PostgresPlatformBusinessPasswordResetService(
        PlatformPostgresConnectionFactory connections,
        SysAdminPasswordHasher passwordHasher)
    {
        _connections = connections;
        _passwordHasher = passwordHasher;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists account_password_reset_tokens (
              id uuid primary key default gen_random_uuid(),
              realm text not null default 'business',
              account_id uuid not null,
              token_hash text not null,
              expires_at timestamptz not null,
              used_at timestamptz,
              requested_ip text,
              created_at timestamptz not null default now(),
              constraint account_password_reset_tokens_realm_chk
                check (realm in ('business','sysadmin'))
            );
            create unique index if not exists ux_password_reset_tokens_hash
              on account_password_reset_tokens (token_hash);
            create index if not exists ix_password_reset_tokens_active
              on account_password_reset_tokens (account_id, expires_at desc)
              where used_at is null;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PasswordResetIssueResult?> IssueTokenAsync(
        string email,
        string? requestedIp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalized = email.Trim().ToLowerInvariant();

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);

        // Look up the user. Active accounts only — we don't issue
        // tokens for disabled / locked accounts (operator should get
        // unstuck through the SysAdmin path, not bypass it).
        UserId accountId;
        string accountEmail;
        string displayName;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = """
                select id,
                       email,
                       coalesce(nullif(display_name, ''), nullif(username, ''), email) as display_name,
                       status
                  from users
                 where lower(email) = @email
                 limit 1;
                """;
            lookup.Parameters.AddWithValue("email", normalized);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            var status = reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant();
            if (status != "active")
            {
                return null;
            }
            accountId = UserId.Parse(reader.GetString(reader.GetOrdinal("id")));
            accountEmail = reader.GetString(reader.GetOrdinal("email")).Trim();
            displayName = reader.GetString(reader.GetOrdinal("display_name")).Trim();
        }

        // Mint a token. Plaintext is returned exactly once; only the
        // hash hits the DB.
        var tokenBytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        var plaintext = Base64UrlEncode(tokenBytes);
        var tokenHash = HashToken(plaintext);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(TokenLifetimeMinutes);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Invalidate any previously-issued unused tokens for this
        // account so re-running "Forgot password" replaces the old
        // link instead of stacking.
        await using (var invalidate = connection.CreateCommand())
        {
            invalidate.Transaction = transaction;
            invalidate.CommandText = """
                update account_password_reset_tokens
                   set used_at = now()
                 where account_id = @account_id
                   and used_at is null;
                """;
            invalidate.Parameters.AddWithValue("account_id", accountId);
            await invalidate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                insert into account_password_reset_tokens
                  (realm, account_id, token_hash, expires_at, requested_ip)
                values
                  ('business', @account_id, @token_hash, @expires_at, @ip);
                """;
            insert.Parameters.AddWithValue("account_id", accountId);
            insert.Parameters.AddWithValue("token_hash", tokenHash);
            insert.Parameters.AddWithValue("expires_at", expiresAt);
            insert.Parameters.AddWithValue("ip", (object?)requestedIp ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new PasswordResetIssueResult(
            PlaintextToken: plaintext,
            AccountId: accountId,
            Email: accountEmail,
            DisplayName: displayName,
            ExpiresAtUtc: expiresAt);
    }

    public async Task<PasswordResetRedeemResult> RedeemTokenAsync(
        string plaintextToken,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            return new PasswordResetRedeemResult(false, "missing_token", "Reset token is required.");
        }
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < MinPasswordLength)
        {
            return new PasswordResetRedeemResult(
                false, "weak_password", $"Password must be at least {MinPasswordLength} characters.");
        }

        var tokenHash = HashToken(plaintextToken);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        UserId accountId;
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                update account_password_reset_tokens
                   set used_at = now()
                 where token_hash = @token_hash
                   and used_at is null
                   and expires_at > now()
                returning account_id;
                """;
            consume.Parameters.AddWithValue("token_hash", tokenHash);

            await using var reader = await consume.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new PasswordResetRedeemResult(
                    false,
                    "invalid_or_expired_token",
                    "This reset link is no longer valid. Request a new one.");
            }
            accountId = UserId.Parse(reader.GetString(0));
        }

        var newHash = _passwordHasher.HashPassword(newPassword);

        await using (var updateUser = connection.CreateCommand())
        {
            updateUser.Transaction = transaction;
            updateUser.CommandText = """
                update users
                   set password_hash = @hash,
                       security_stamp = encode(gen_random_bytes(16), 'hex'),
                       updated_at = now()
                 where id = @id;
                """;
            updateUser.Parameters.AddWithValue("hash", newHash);
            updateUser.Parameters.AddWithValue("id", accountId);
            await updateUser.ExecuteNonQueryAsync(cancellationToken);
        }

        // Revoke every active session for this account so a stolen
        // session cookie on another device can't survive the reset.
        await using (var revokeSessions = connection.CreateCommand())
        {
            revokeSessions.Transaction = transaction;
            revokeSessions.CommandText = """
                update business_sessions
                   set revoked_at = now()
                 where user_id = @id
                   and revoked_at is null;
                """;
            revokeSessions.Parameters.AddWithValue("id", accountId);
            await revokeSessions.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new PasswordResetRedeemResult(true, null, null);
    }

    private static string HashToken(string plaintext)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
