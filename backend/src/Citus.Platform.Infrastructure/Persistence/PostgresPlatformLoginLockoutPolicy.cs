using System.Security.Cryptography;
using System.Text;
using Citus.Platform.Core.Abstractions;

namespace Citus.Platform.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed brute-force protection. See
/// <see cref="IPlatformLoginLockoutPolicy"/> for the policy summary.
/// Schema lives on the same platform DB the auth repos use; both
/// <c>account_login_attempts</c> and <c>account_lockouts</c> are
/// indexed on (email_hash, attempted_at desc) so the threshold counts
/// stay cheap even with years of history.
/// </summary>
public sealed class PostgresPlatformLoginLockoutPolicy : IPlatformLoginLockoutPolicy
{
    private const int FailureThreshold = 5;
    private const int FailureWindowMinutes = 15;
    private const int TempLockoutMinutes = 15;
    private const int PermanentTriggerCount = 3;
    private const int PermanentTriggerWindowHours = 36;

    private readonly PlatformPostgresConnectionFactory _connections;

    public PostgresPlatformLoginLockoutPolicy(PlatformPostgresConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists account_login_attempts (
              id uuid primary key default gen_random_uuid(),
              realm text not null,
              account_id uuid,
              email_hash text not null,
              remote_ip text,
              user_agent text,
              succeeded boolean not null,
              attempted_at timestamptz not null default now(),
              constraint account_login_attempts_realm_chk
                check (realm in ('sysadmin','business'))
            );
            create index if not exists ix_login_attempts_lookup
              on account_login_attempts (realm, email_hash, attempted_at desc);

            create table if not exists account_lockouts (
              id uuid primary key default gen_random_uuid(),
              realm text not null,
              email_hash text not null,
              account_id uuid,
              lockout_kind text not null,
              locked_at timestamptz not null default now(),
              locked_until timestamptz,
              lifted_at timestamptz,
              lifted_by_sysadmin_id uuid,
              lifted_reason text,
              constraint account_lockouts_realm_chk
                check (realm in ('sysadmin','business')),
              constraint account_lockouts_kind_chk
                check (lockout_kind in ('temporary_15min','permanent'))
            );
            create index if not exists ix_lockouts_active
              on account_lockouts (realm, email_hash, locked_at desc)
              where lifted_at is null;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LoginLockoutCheck> CheckAsync(
        string realm,
        string email,
        CancellationToken cancellationToken)
    {
        if (!LoginLockoutRealms.IsValid(realm) || string.IsNullOrWhiteSpace(email))
        {
            return new LoginLockoutCheck(false, null, null, null);
        }

        var emailHash = HashEmail(email);

        const string sql = """
            select id, lockout_kind, locked_until
              from account_lockouts
             where realm = @realm
               and email_hash = @email_hash
               and lifted_at is null
               and (locked_until is null or locked_until > now())
             order by locked_at desc
             limit 1;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("realm", realm);
        command.Parameters.AddWithValue("email_hash", emailHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LoginLockoutCheck(false, null, null, null);
        }

        var kind = reader.GetString(1);
        DateTimeOffset? until = reader.IsDBNull(2)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(2);

        var message = kind switch
        {
            LoginLockoutKinds.Permanent =>
                "This account is locked. Contact your administrator to restore access.",
            _ when until is { } u =>
                $"Too many failed attempts. Try again after {u.ToLocalTime():HH:mm} ({Math.Max(1, (int)Math.Ceiling((u - DateTimeOffset.UtcNow).TotalMinutes))} min).",
            _ => "Too many failed attempts. Try again shortly.",
        };

        return new LoginLockoutCheck(true, kind, until, message);
    }

    public async Task RecordAttemptAsync(
        LoginAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (!LoginLockoutRealms.IsValid(attempt.Realm) || string.IsNullOrWhiteSpace(attempt.Email))
        {
            return;
        }

        var emailHash = HashEmail(attempt.Email);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. Always insert the attempt — every row is auditable.
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                insert into account_login_attempts
                  (realm, account_id, email_hash, remote_ip, user_agent, succeeded)
                values
                  (@realm, @account_id, @email_hash, @remote_ip, @user_agent, @succeeded);
                """;
            insert.Parameters.AddWithValue("realm", attempt.Realm);
            insert.Parameters.AddWithValue("account_id", (object?)attempt.AccountId ?? DBNull.Value);
            insert.Parameters.AddWithValue("email_hash", emailHash);
            insert.Parameters.AddWithValue("remote_ip", (object?)attempt.RemoteIp ?? DBNull.Value);
            insert.Parameters.AddWithValue("user_agent", (object?)attempt.UserAgent ?? DBNull.Value);
            insert.Parameters.AddWithValue("succeeded", attempt.Succeeded);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        if (attempt.Succeeded)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // 2. Count failures in the last FailureWindowMinutes for this realm + email.
        int failureCount;
        await using (var countFails = connection.CreateCommand())
        {
            countFails.Transaction = transaction;
            countFails.CommandText = """
                select count(*)::int
                  from account_login_attempts
                 where realm = @realm
                   and email_hash = @email_hash
                   and succeeded = false
                   and attempted_at >= now() - make_interval(mins => @window_minutes);
                """;
            countFails.Parameters.AddWithValue("realm", attempt.Realm);
            countFails.Parameters.AddWithValue("email_hash", emailHash);
            countFails.Parameters.AddWithValue("window_minutes", FailureWindowMinutes);
            failureCount = (int)(await countFails.ExecuteScalarAsync(cancellationToken))!;
        }

        if (failureCount < FailureThreshold)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // 3. Threshold hit — but only insert a fresh temporary lockout
        // if there isn't already one in effect (avoid stacking).
        bool hasActiveLockout;
        await using (var checkActive = connection.CreateCommand())
        {
            checkActive.Transaction = transaction;
            checkActive.CommandText = """
                select exists (
                  select 1
                    from account_lockouts
                   where realm = @realm
                     and email_hash = @email_hash
                     and lifted_at is null
                     and (locked_until is null or locked_until > now())
                );
                """;
            checkActive.Parameters.AddWithValue("realm", attempt.Realm);
            checkActive.Parameters.AddWithValue("email_hash", emailHash);
            hasActiveLockout = (bool)(await checkActive.ExecuteScalarAsync(cancellationToken))!;
        }

        if (hasActiveLockout)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // 4. Insert the temporary lockout.
        await using (var insertLockout = connection.CreateCommand())
        {
            insertLockout.Transaction = transaction;
            insertLockout.CommandText = """
                insert into account_lockouts
                  (realm, email_hash, account_id, lockout_kind, locked_until)
                values
                  (@realm, @email_hash, @account_id, @kind,
                   now() + make_interval(mins => @lock_minutes));
                """;
            insertLockout.Parameters.AddWithValue("realm", attempt.Realm);
            insertLockout.Parameters.AddWithValue("email_hash", emailHash);
            insertLockout.Parameters.AddWithValue("account_id", (object?)attempt.AccountId ?? DBNull.Value);
            insertLockout.Parameters.AddWithValue("kind", LoginLockoutKinds.Temporary15Min);
            insertLockout.Parameters.AddWithValue("lock_minutes", TempLockoutMinutes);
            await insertLockout.ExecuteNonQueryAsync(cancellationToken);
        }

        // 5. Count temporary lockouts in the last 36h. ≥3 → permanent.
        int tempLockoutCount;
        await using (var countLockouts = connection.CreateCommand())
        {
            countLockouts.Transaction = transaction;
            countLockouts.CommandText = """
                select count(*)::int
                  from account_lockouts
                 where realm = @realm
                   and email_hash = @email_hash
                   and lockout_kind = @kind
                   and locked_at >= now() - make_interval(hours => @window_hours);
                """;
            countLockouts.Parameters.AddWithValue("realm", attempt.Realm);
            countLockouts.Parameters.AddWithValue("email_hash", emailHash);
            countLockouts.Parameters.AddWithValue("kind", LoginLockoutKinds.Temporary15Min);
            countLockouts.Parameters.AddWithValue("window_hours", PermanentTriggerWindowHours);
            tempLockoutCount = (int)(await countLockouts.ExecuteScalarAsync(cancellationToken))!;
        }

        if (tempLockoutCount < PermanentTriggerCount)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // 6. Permanent lockout — insert + mirror to the account's status.
        await using (var insertPermanent = connection.CreateCommand())
        {
            insertPermanent.Transaction = transaction;
            insertPermanent.CommandText = """
                insert into account_lockouts
                  (realm, email_hash, account_id, lockout_kind, locked_until)
                values
                  (@realm, @email_hash, @account_id, @kind, null);
                """;
            insertPermanent.Parameters.AddWithValue("realm", attempt.Realm);
            insertPermanent.Parameters.AddWithValue("email_hash", emailHash);
            insertPermanent.Parameters.AddWithValue("account_id", (object?)attempt.AccountId ?? DBNull.Value);
            insertPermanent.Parameters.AddWithValue("kind", LoginLockoutKinds.Permanent);
            await insertPermanent.ExecuteNonQueryAsync(cancellationToken);
        }

        if (attempt.AccountId is { } accountId)
        {
            await UpdateAccountStatusAsync(
                connection, transaction, attempt.Realm, accountId, "locked", cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LockoutSummary>> ListActiveLockoutsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            with active as (
              select id, realm, email_hash, account_id, lockout_kind,
                     locked_at, locked_until
                from account_lockouts
               where lifted_at is null
                 and (locked_until is null or locked_until > now())
            )
            select a.id, a.realm, a.email_hash, a.account_id, a.lockout_kind,
                   a.locked_at, a.locked_until,
                   coalesce(
                     (select count(*)::int
                        from account_login_attempts att
                       where att.realm = a.realm
                         and att.email_hash = a.email_hash
                         and att.succeeded = false
                         and att.attempted_at >= a.locked_at - interval '15 minutes'),
                     0) as recent_failure_count,
                   coalesce(
                     (select string_agg(distinct lower(u.email), ',')
                        from users u
                       where a.realm = 'business'
                         and u.id = a.account_id),
                     (select string_agg(distinct lower(s.email), ',')
                        from sysadmin_accounts s
                       where a.realm = 'sysadmin'
                         and s.id = a.account_id)
                   ) as resolved_email
              from active a
             order by a.locked_at desc
             limit 200;
            """;

        var result = new List<LockoutSummary>();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var resolvedEmail = reader.IsDBNull(7) ? null : reader.GetString(7);
            var maskedEmail = MaskEmail(resolvedEmail);
            result.Add(new LockoutSummary(
                Id: reader.GetGuid(0),
                Realm: reader.GetString(1),
                MaskedEmail: maskedEmail,
                AccountId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                LockoutKind: reader.GetString(4),
                LockedAt: reader.GetFieldValue<DateTimeOffset>(5),
                LockedUntil: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                RecentFailureCount: reader.GetInt32(reader.GetOrdinal("recent_failure_count"))));
        }

        return result;
    }

    public async Task<LockoutLiftResult> LiftLockoutAsync(
        Guid lockoutId,
        Guid sysAdminAccountId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new LockoutLiftResult(false, "Reason is required.");
        }

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string? realm;
        Guid? accountId;
        string? kind;

        await using (var lift = connection.CreateCommand())
        {
            lift.Transaction = transaction;
            lift.CommandText = """
                update account_lockouts
                   set lifted_at = now(),
                       lifted_by_sysadmin_id = @sysadmin_id,
                       lifted_reason = @reason
                 where id = @id
                   and lifted_at is null
                returning realm, account_id, lockout_kind;
                """;
            lift.Parameters.AddWithValue("id", lockoutId);
            lift.Parameters.AddWithValue("sysadmin_id", sysAdminAccountId);
            lift.Parameters.AddWithValue("reason", reason.Trim());

            await using var reader = await lift.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new LockoutLiftResult(false, "Lockout not found or already lifted.");
            }
            realm = reader.GetString(0);
            accountId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            kind = reader.GetString(2);
        }

        // Permanent lockouts mirror to account.status='locked'. When
        // lifted, mirror back to 'active' — but only if there isn't
        // *another* active permanent lockout still in effect for the
        // same account (defensive: someone could have stacked them).
        if (kind == LoginLockoutKinds.Permanent && accountId is { } id)
        {
            bool stillLocked;
            await using (var checkOther = connection.CreateCommand())
            {
                checkOther.Transaction = transaction;
                checkOther.CommandText = """
                    select exists (
                      select 1 from account_lockouts
                       where account_id = @account_id
                         and realm = @realm
                         and lockout_kind = 'permanent'
                         and lifted_at is null
                    );
                    """;
                checkOther.Parameters.AddWithValue("account_id", id);
                checkOther.Parameters.AddWithValue("realm", realm!);
                stillLocked = (bool)(await checkOther.ExecuteScalarAsync(cancellationToken))!;
            }

            if (!stillLocked)
            {
                await UpdateAccountStatusAsync(
                    connection, transaction, realm!, id, "active", cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new LockoutLiftResult(true, null);
    }

    private static async Task UpdateAccountStatusAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string realm,
        Guid accountId,
        string newStatus,
        CancellationToken cancellationToken)
    {
        var sql = realm switch
        {
            LoginLockoutRealms.SysAdmin =>
                "update sysadmin_accounts set status = @status, updated_at = now() where id = @id;",
            LoginLockoutRealms.Business =>
                "update users set status = @status, updated_at = now() where id = @id;",
            _ => null,
        };
        if (sql is null) return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("status", newStatus);
        command.Parameters.AddWithValue("id", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string HashEmail(string email)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    private static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "(unknown)";
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var local = email[..at];
        var domain = email[(at + 1)..];
        var keep = Math.Min(2, local.Length);
        return $"{local[..keep]}***@{domain}";
    }
}
