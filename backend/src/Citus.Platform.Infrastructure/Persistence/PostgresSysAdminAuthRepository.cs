using System.Security.Cryptography;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Npgsql;
using System.Text.Json;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PostgresSysAdminAuthRepository(
    PlatformPostgresConnectionFactory connectionFactory,
    SysAdminPasswordHasher passwordHasher,
    IPlatformRuntimeStateRepository runtimeStateRepository,
    IPlatformLoginLockoutPolicy lockoutPolicy) : ISysAdminAuthRepository
{
    private const int MinimumSecretLength = 12;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create extension if not exists pgcrypto;

            create table if not exists sysadmin_accounts (
              id char(7) primary key,
              email text not null unique,
              display_name text not null default '',
              password_hash text not null,
              status text not null default 'active',
              last_login_at timestamptz,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint sysadmin_accounts_status_chk check (status in ('active', 'disabled', 'locked'))
            );

            alter table sysadmin_accounts
              add column if not exists display_name text not null default '';

            create table if not exists sysadmin_sessions (
              id uuid primary key default gen_random_uuid(),
              sysadmin_account_id char(7) not null references sysadmin_accounts(id) on delete cascade,
              session_token_hash text not null unique,
              expires_at timestamptz not null,
              last_seen_at timestamptz not null default now(),
              revoked_at timestamptz,
              remote_ip text,
              user_agent text,
              created_at timestamptz not null default now()
            );

            create index if not exists idx_sysadmin_accounts_status_email
              on sysadmin_accounts (status, email);

            create index if not exists idx_sysadmin_sessions_active
              on sysadmin_sessions (sysadmin_account_id, expires_at desc)
              where revoked_at is null;
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SysAdminSetupStatus> GetSetupStatusAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await runtimeStateRepository.EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              (select count(*)::int from sysadmin_accounts) as account_count,
              case
                when to_regclass('public.companies') is null then 0
                else (select count(*)::int from companies)
              end as company_count,
              case
                when to_regclass('public.company_memberships') is null then 0
                else (
                  select count(*)::int
                  from company_memberships
                  where is_active = true
                    and role = 'owner'
                )
              end as owner_membership_count;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        var firstCompanySetupState = await runtimeStateRepository.GetFirstCompanySetupStateAsync(cancellationToken);

        return new SysAdminSetupStatus
        {
            AccountCount = reader.GetInt32(reader.GetOrdinal("account_count")),
            CompanyCount = reader.GetInt32(reader.GetOrdinal("company_count")),
            OwnerMembershipCount = reader.GetInt32(reader.GetOrdinal("owner_membership_count")),
            FirstCompanySetupDeferred = firstCompanySetupState?.IsDeferred == true,
            FirstCompanySetupDeferredAtUtc = firstCompanySetupState?.DeferredAtUtc
        };
    }

    public async Task EnsureBootstrapAccountAsync(
        string email,
        string password,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName) ? "Platform Administrator" : displayName.Trim();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var existsCommand = connection.CreateCommand();
        existsCommand.Transaction = transaction;
        existsCommand.CommandText =
            """
            select id
            from sysadmin_accounts
            where lower(email) = @email
            limit 1;
            """;
        existsCommand.Parameters.AddWithValue("email", normalizedEmail);

        var existingId = await existsCommand.ExecuteScalarAsync(cancellationToken);
        if (existingId is null or DBNull)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into sysadmin_accounts (
                  email,
                  display_name,
                  password_hash,
                  status
                )
                values (
                  @email,
                  @display_name,
                  @password_hash,
                  'active'
                );
                """;
            insertCommand.Parameters.AddWithValue("email", normalizedEmail);
            insertCommand.Parameters.AddWithValue("display_name", normalizedDisplayName);
            insertCommand.Parameters.AddWithValue("password_hash", passwordHasher.HashPassword(password));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SysAdminFirstAccountProvisioningResult> ProvisionFirstAccountAsync(
        string email,
        string password,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return FailedProvisioning("missing_email", "SysAdmin email is required.");
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? "Platform Administrator"
            : displayName.Trim();
        var secretValidationError = ValidateSecret(password, currentPassword: null);
        if (!string.IsNullOrWhiteSpace(secretValidationError))
        {
            return FailedProvisioning("invalid_secret", secretValidationError);
        }

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "lock table sysadmin_accounts in access exclusive mode;";
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var existingStatus = await ReadSetupStatusAsync(connection, transaction, cancellationToken);
        if (existingStatus.HasAnyAccount)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FailedProvisioning(
                "already_provisioned",
                "The first SysAdmin account has already been provisioned.");
        }

        var accountId = UserId.FromOrdinal(1);
        var provisionedAtUtc = DateTimeOffset.UtcNow;

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into sysadmin_accounts (
                  id,
                  email,
                  display_name,
                  password_hash,
                  status,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @email,
                  @display_name,
                  @password_hash,
                  'active',
                  @created_at,
                  @updated_at
                );
                """;
            insertCommand.Parameters.AddWithValue("id", accountId);
            insertCommand.Parameters.AddWithValue("email", normalizedEmail);
            insertCommand.Parameters.AddWithValue("display_name", normalizedDisplayName);
            insertCommand.Parameters.AddWithValue("password_hash", passwordHasher.HashPassword(password));
            insertCommand.Parameters.AddWithValue("created_at", provisionedAtUtc);
            insertCommand.Parameters.AddWithValue("updated_at", provisionedAtUtc);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditLogIfAvailableAsync(
            connection,
            transaction,
            actorType: "system",
            actorId: null,
            entityType: "sysadmin_account",
            entityId: accountId,
            action: "sysadmin_first_account_created",
            payload: JsonSerializer.Serialize(new
            {
                email = normalizedEmail,
                display_name = normalizedDisplayName,
                provisioning_mode = "first_sysadmin"
            }),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new SysAdminFirstAccountProvisioningResult
        {
            Succeeded = true,
            SysAdminAccountId = accountId,
            Email = normalizedEmail,
            DisplayName = normalizedDisplayName,
            ProvisionedAtUtc = provisionedAtUtc
        };
    }

    public async Task<SysAdminAuthenticationResult> AuthenticateAsync(
        string email,
        string password,
        TimeSpan sessionLifetime,
        string? remoteIp,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return FailedAuthentication("invalid_credentials", "Email and password are required.");
        }

        await EnsureSchemaAsync(cancellationToken);
        await lockoutPolicy.EnsureSchemaAsync(cancellationToken);

        var normalizedEmail = email.Trim().ToLowerInvariant();

        // Lockout gate runs before password verification so a locked
        // account can't be probed for password validity (timing /
        // error-message enumeration).
        var lockoutCheck = await lockoutPolicy.CheckAsync(
            LoginLockoutRealms.SysAdmin, normalizedEmail, cancellationToken);
        if (lockoutCheck.IsBlocked)
        {
            return FailedAuthentication(
                lockoutCheck.BlockKind == LoginLockoutKinds.Permanent
                    ? "account_permanently_locked"
                    : "account_temporarily_locked",
                lockoutCheck.Message ?? "Account is locked.");
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var account = await ReadAccountByEmailAsync(connection, transaction, normalizedEmail, cancellationToken);
        if (account is null || !passwordHasher.VerifyPassword(password, account.PasswordHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            await lockoutPolicy.RecordAttemptAsync(
                new LoginAttempt(
                    Realm: LoginLockoutRealms.SysAdmin,
                    Email: normalizedEmail,
                    AccountId: account?.Id,
                    RemoteIp: remoteIp,
                    UserAgent: userAgent,
                    Succeeded: false),
                cancellationToken);
            return FailedAuthentication("invalid_credentials", "The email or password is invalid.");
        }

        if (!string.Equals(account.Status, "active", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            // Don't ratchet failures for status='locked' / 'disabled' —
            // it's already locked, no signal to add.
            return FailedAuthentication("account_not_active", $"SysAdmin account is {account.Status}.");
        }

        var sessionToken = CreateSessionToken();
        var sessionTokenHash = HashSessionToken(sessionToken);
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(sessionLifetime);

        await using (var insertSession = connection.CreateCommand())
        {
            insertSession.Transaction = transaction;
            insertSession.CommandText =
                """
                insert into sysadmin_sessions (
                  sysadmin_account_id,
                  session_token_hash,
                  expires_at,
                  last_seen_at,
                  remote_ip,
                  user_agent
                )
                values (
                  @sysadmin_account_id,
                  @session_token_hash,
                  @expires_at,
                  now(),
                  @remote_ip,
                  @user_agent
                );
                """;
            insertSession.Parameters.AddWithValue("sysadmin_account_id", account.Id);
            insertSession.Parameters.AddWithValue("session_token_hash", sessionTokenHash);
            insertSession.Parameters.AddWithValue("expires_at", expiresAtUtc);
            insertSession.Parameters.AddWithValue("remote_ip", (object?)NormalizeOptional(remoteIp) ?? DBNull.Value);
            insertSession.Parameters.AddWithValue("user_agent", (object?)NormalizeOptional(userAgent) ?? DBNull.Value);
            await insertSession.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateAccount = connection.CreateCommand())
        {
            updateAccount.Transaction = transaction;
            updateAccount.CommandText =
                """
                update sysadmin_accounts
                set last_login_at = now(),
                    updated_at = now()
                where id = @id;
                """;
            updateAccount.Parameters.AddWithValue("id", account.Id);
            await updateAccount.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await lockoutPolicy.RecordAttemptAsync(
            new LoginAttempt(
                Realm: LoginLockoutRealms.SysAdmin,
                Email: normalizedEmail,
                AccountId: account.Id,
                RemoteIp: remoteIp,
                UserAgent: userAgent,
                Succeeded: true),
            cancellationToken);

        return new SysAdminAuthenticationResult
        {
            Succeeded = true,
            SessionToken = sessionToken,
            SysAdminAccountId = account.Id,
            Email = account.Email,
            DisplayName = account.DisplayName,
            Roles = ["sysadmin"],
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public async Task<SysAdminSessionValidationResult> ValidateSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return FailedValidation("missing_session", "SysAdmin session token is required.");
        }

        await EnsureSchemaAsync(cancellationToken);

        var sessionTokenHash = HashSessionToken(sessionToken.Trim());

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var session = await ReadSessionAsync(connection, transaction, sessionTokenHash, cancellationToken);
        if (session is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FailedValidation("invalid_session", "SysAdmin session was not found.");
        }

        if (session.RevokedAtUtc.HasValue || session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FailedValidation("expired_session", "SysAdmin session has expired or was revoked.");
        }

        if (!string.Equals(session.AccountStatus, "active", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return FailedValidation("account_not_active", $"SysAdmin account is {session.AccountStatus}.");
        }

        await using (var updateSession = connection.CreateCommand())
        {
            updateSession.Transaction = transaction;
            updateSession.CommandText =
                """
                update sysadmin_sessions
                set last_seen_at = now()
                where id = @id;
                """;
            updateSession.Parameters.AddWithValue("id", session.SessionId);
            await updateSession.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new SysAdminSessionValidationResult
        {
            Succeeded = true,
            SysAdminAccountId = session.AccountId,
            Email = session.Email,
            DisplayName = session.DisplayName,
            Roles = ["sysadmin"],
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
            update sysadmin_sessions
            set revoked_at = now()
            where session_token_hash = @session_token_hash
              and revoked_at is null;
            """;
        command.Parameters.AddWithValue("session_token_hash", HashSessionToken(sessionToken.Trim()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SysAdminSecretRotationResult> RotateSecretAsync(
        UserId sysAdminAccountId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (sysAdminAccountId.Value is null)
        {
            return FailedRotation("missing_account", "SysAdmin account id is required.");
        }

        if (string.IsNullOrWhiteSpace(currentPassword))
        {
            return FailedRotation("missing_current_password", "Current password is required.");
        }

        var secretValidationError = ValidateSecret(newPassword, currentPassword);
        if (!string.IsNullOrWhiteSpace(secretValidationError))
        {
            return FailedRotation("invalid_secret", secretValidationError);
        }

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var account = await ReadAccountByIdAsync(connection, transaction, sysAdminAccountId, cancellationToken);
        if (account is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FailedRotation("not_found", "SysAdmin account was not found.");
        }

        if (!passwordHasher.VerifyPassword(currentPassword, account.PasswordHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return FailedRotation("invalid_current_password", "Current password is invalid.");
        }

        if (passwordHasher.VerifyPassword(newPassword, account.PasswordHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return FailedRotation("secret_reused", "New password must differ from the current password.");
        }

        var rotatedAtUtc = DateTimeOffset.UtcNow;

        await using (var updateAccount = connection.CreateCommand())
        {
            updateAccount.Transaction = transaction;
            updateAccount.CommandText =
                """
                update sysadmin_accounts
                set password_hash = @password_hash,
                    updated_at = @updated_at
                where id = @id;
                """;
            updateAccount.Parameters.AddWithValue("id", sysAdminAccountId);
            updateAccount.Parameters.AddWithValue("password_hash", passwordHasher.HashPassword(newPassword));
            updateAccount.Parameters.AddWithValue("updated_at", rotatedAtUtc);
            await updateAccount.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var revokeSessions = connection.CreateCommand())
        {
            revokeSessions.Transaction = transaction;
            revokeSessions.CommandText =
                """
                update sysadmin_sessions
                set revoked_at = now()
                where sysadmin_account_id = @sysadmin_account_id
                  and revoked_at is null;
                """;
            revokeSessions.Parameters.AddWithValue("sysadmin_account_id", sysAdminAccountId);
            await revokeSessions.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditLogIfAvailableAsync(
            connection,
            transaction,
            actorType: "sysadmin",
            actorId: sysAdminAccountId,
            entityType: "sysadmin_account",
            entityId: sysAdminAccountId,
            action: "sysadmin_password_rotated",
            payload: JsonSerializer.Serialize(new
            {
                email = account.Email,
                rotation_mode = "self_service",
                sessions_revoked = true
            }),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new SysAdminSecretRotationResult
        {
            Succeeded = true,
            SysAdminAccountId = account.Id,
            Email = account.Email,
            DisplayName = account.DisplayName,
            RotatedAtUtc = rotatedAtUtc
        };
    }

    private static SysAdminAuthenticationResult FailedAuthentication(string code, string message) =>
        new()
        {
            Succeeded = false,
            FailureCode = code,
            FailureMessage = message
        };

    private static SysAdminFirstAccountProvisioningResult FailedProvisioning(string code, string message) =>
        new()
        {
            Succeeded = false,
            FailureCode = code,
            FailureMessage = message
        };

    private static SysAdminSecretRotationResult FailedRotation(string code, string message) =>
        new()
        {
            Succeeded = false,
            FailureCode = code,
            FailureMessage = message
        };

    private static SysAdminSessionValidationResult FailedValidation(string code, string message) =>
        new()
        {
            Succeeded = false,
            FailureCode = code,
            FailureMessage = message
        };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string CreateSessionToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private static string HashSessionToken(string sessionToken)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionToken));
        return Convert.ToHexString(bytes);
    }

    private static async Task<SysAdminAccountRecord?> ReadAccountByEmailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string email,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id,
                   email,
                   coalesce(nullif(display_name, ''), email) as display_name,
                   password_hash,
                   status
            from sysadmin_accounts
            where lower(email) = @email
            limit 1;
            """;
        command.Parameters.AddWithValue("email", email);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SysAdminAccountRecord(
            UserId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.GetString(reader.GetOrdinal("display_name")).Trim(),
            reader.GetString(reader.GetOrdinal("password_hash")).Trim(),
            reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant());
    }

    private static async Task<SysAdminAccountRecord?> ReadAccountByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id,
                   email,
                   coalesce(nullif(display_name, ''), email) as display_name,
                   password_hash,
                   status
            from sysadmin_accounts
            where id = @id
            for update;
            """;
        command.Parameters.AddWithValue("id", sysAdminAccountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SysAdminAccountRecord(
            UserId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.GetString(reader.GetOrdinal("display_name")).Trim(),
            reader.GetString(reader.GetOrdinal("password_hash")).Trim(),
            reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant());
    }

    private static async Task<SysAdminSetupStatus> ReadSetupStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select count(*) from sysadmin_accounts;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        var accountCount = scalar switch
        {
            long value => (int)value,
            int value => value,
            _ => 0
        };

        return new SysAdminSetupStatus
        {
            AccountCount = accountCount
        };
    }

    private static async Task<SysAdminSessionRecord?> ReadSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionTokenHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select session.id,
                   account.id as account_id,
                   account.email,
                   coalesce(nullif(account.display_name, ''), account.email) as display_name,
                   account.status as account_status,
                   session.expires_at,
                   session.revoked_at
            from sysadmin_sessions session
            inner join sysadmin_accounts account
              on account.id = session.sysadmin_account_id
            where session.session_token_hash = @session_token_hash
            limit 1;
            """;
        command.Parameters.AddWithValue("session_token_hash", sessionTokenHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SysAdminSessionRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            UserId.Parse(reader.GetString(reader.GetOrdinal("account_id"))),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.GetString(reader.GetOrdinal("display_name")).Trim(),
            reader.GetString(reader.GetOrdinal("account_status")).Trim().ToLowerInvariant(),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("expires_at")),
            reader.IsDBNull(reader.GetOrdinal("revoked_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("revoked_at")));
    }

    private sealed record SysAdminAccountRecord(
        UserId Id,
        string Email,
        string DisplayName,
        string PasswordHash,
        string Status);

    private sealed record SysAdminSessionRecord(
        Guid SessionId,
        UserId AccountId,
        string Email,
        string DisplayName,
        string AccountStatus,
        DateTimeOffset ExpiresAtUtc,
        DateTimeOffset? RevokedAtUtc);

    private static string? ValidateSecret(string secret, string? currentPassword)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "Password is required.";
        }

        var trimmed = secret.Trim();
        if (trimmed.Length < MinimumSecretLength)
        {
            return $"Password must be at least {MinimumSecretLength} characters.";
        }

        if (!string.IsNullOrWhiteSpace(currentPassword) &&
            string.Equals(trimmed, currentPassword.Trim(), StringComparison.Ordinal))
        {
            return "New password must differ from the current password.";
        }

        return null;
    }

    private static async Task InsertAuditLogIfAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string actorType,
        UserId? actorId,
        string entityType,
        UserId entityId,
        string action,
        string payload,
        CancellationToken cancellationToken)
    {
        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "select to_regclass('public.audit_logs') is not null;";
            var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (exists is not bool available || !available)
            {
                return;
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into audit_logs (
              id,
              company_id,
              actor_type,
              actor_id,
              entity_type,
              entity_id,
              action,
              payload
            )
            values (
              @id,
              null,
              @actor_type,
              @actor_id,
              @entity_type,
              @entity_id,
              @action,
              @payload::jsonb
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("actor_type", actorType);
        command.Parameters.AddWithValue("actor_id", actorId.HasValue ? actorId.Value.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.AddWithValue("entity_id", entityId.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
