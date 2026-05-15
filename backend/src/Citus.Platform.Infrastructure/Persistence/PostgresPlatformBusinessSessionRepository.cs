using System.Security.Cryptography;
using System.Text;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Platform.Core.Runtime;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PostgresPlatformBusinessSessionRepository(
    PlatformPostgresConnectionFactory connectionFactory,
    SysAdminPasswordHasher passwordHasher,
    IPlatformRuntimeStateRepository runtimeStateRepository,
    IPlatformVerificationNotificationSender notificationSender,
    IConfiguration configuration,
    IPlatformLoginLockoutPolicy lockoutPolicy) : IPlatformBusinessSessionRepository
{
    private const string NoMfaMode = "none";
    private const string EmailCodeMfaMode = "email_code";
    private const string TotpAppMfaMode = "totp_app";
    private const int MaxMfaChallengeFailures = 5;
    private static readonly TimeSpan MfaChallengeLockoutDuration = TimeSpan.FromMinutes(15);
    private readonly PlatformTotpSecretProtector totpSecretProtector = new(configuration);

    // Stage-1.4: cache + information_schema probe so the 7 ALTERs
    // (and the one-shot security_stamp_snapshot backfill UPDATEs)
    // only run on a fresh DB or until the deploy-time migration
    // runner gets the same SQL. Same pattern as commit 2ef2640.
    private static volatile bool _schemaEnsured;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        // Probe a column on `business_sessions` — the only table this
        // helper creates that no other helper (Governance, AccountProfile)
        // touches. Using a shared column on `business_session_mfa_challenges`
        // would let Governance's inline CREATE flip our cache to true and
        // make us skip the unique CREATE TABLE business_sessions, breaking
        // the next business login.
        await using (var probeConnection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        await using (var probe = probeConnection.CreateCommand())
        {
            probe.CommandText =
                """
                select count(*)
                from information_schema.columns
                where table_schema = 'public'
                  and table_name = 'business_sessions'
                  and column_name = 'security_stamp_snapshot';
                """;
            var present = Convert.ToInt32(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
            if (present == 1)
            {
                _schemaEnsured = true;
                return;
            }
        }

        const string sql = """
            create extension if not exists pgcrypto;

            alter table users
              add column if not exists status text not null default 'active';

            alter table users
              add column if not exists locked_until timestamptz;

            alter table users
              add column if not exists mfa_mode text not null default 'none';

            create table if not exists business_sessions (
              id uuid primary key default gen_random_uuid(),
              token_hash text not null unique,
              user_id char(7) not null references users(id) on delete cascade,
              active_company_id char(7) not null references companies(id) on delete restrict,
              membership_id uuid not null references company_memberships(id) on delete cascade,
              role text not null,
              permissions jsonb not null default '[]'::jsonb,
              company_status text not null,
              permission_version text,
              security_stamp_snapshot text not null default '',
              expires_at timestamptz not null,
              revoked_at timestamptz,
              created_at timestamptz not null default now(),
              constraint business_sessions_role_chk check (role in ('owner', 'user')),
              constraint business_sessions_permissions_array_chk check (jsonb_typeof(permissions) = 'array'),
              constraint business_sessions_company_status_chk check (company_status in ('active', 'inactive', 'suspended', 'archived'))
            );

            alter table business_sessions
              add column if not exists security_stamp_snapshot text not null default '',
              add column if not exists revoked_at timestamptz;

            update business_sessions s
            set security_stamp_snapshot = u.security_stamp
            from users u
            where s.user_id = u.id
              and coalesce(s.security_stamp_snapshot, '') = '';

            create index if not exists idx_business_sessions_user_company_expiry
              on business_sessions (user_id, active_company_id, expires_at desc);

            create index if not exists idx_business_sessions_token_expiry
              on business_sessions (token_hash, expires_at desc);

            create index if not exists idx_business_sessions_active_user_company_expiry
              on business_sessions (user_id, active_company_id, expires_at desc)
              where revoked_at is null;

            create index if not exists idx_business_sessions_active_token_expiry
              on business_sessions (token_hash, expires_at desc)
              where revoked_at is null;

            create table if not exists business_session_mfa_challenges (
              id uuid primary key default gen_random_uuid(),
              user_id char(7) not null references users(id) on delete cascade,
              active_company_id char(7) not null references companies(id) on delete restrict,
              membership_id uuid not null references company_memberships(id) on delete cascade,
              role text not null,
              permissions jsonb not null default '[]'::jsonb,
              company_status text not null,
              factor text not null,
              destination text not null,
              code_hash text not null,
              security_stamp_snapshot text not null default '',
              expires_at timestamptz not null,
              consumed_at timestamptz,
              failed_attempts integer not null default 0,
              created_at timestamptz not null default now(),
              constraint business_session_mfa_challenges_role_chk check (role in ('owner', 'user')),
              constraint business_session_mfa_challenges_permissions_array_chk check (jsonb_typeof(permissions) = 'array'),
              constraint business_session_mfa_challenges_company_status_chk check (company_status in ('active', 'inactive', 'suspended', 'archived'))
            );

            alter table business_session_mfa_challenges
              drop constraint if exists business_session_mfa_challenges_factor_chk;

            alter table business_session_mfa_challenges
              add constraint business_session_mfa_challenges_factor_chk
              check (factor in ('email_code', 'totp_app'));

            alter table business_session_mfa_challenges
              add column if not exists security_stamp_snapshot text not null default '';

            update business_session_mfa_challenges c
            set security_stamp_snapshot = u.security_stamp
            from users u
            where c.user_id = u.id
              and coalesce(c.security_stamp_snapshot, '') = '';

            create table if not exists account_mfa_totp_enrollments (
              id uuid primary key default gen_random_uuid(),
              user_id char(7) not null references users(id) on delete cascade,
              status text not null,
              secret_base32 text not null,
              created_at timestamptz not null default now(),
              expires_at timestamptz,
              confirmed_at timestamptz,
              revoked_at timestamptz,
              last_used_at timestamptz,
              constraint account_mfa_totp_enrollments_status_chk
                check (status in ('pending', 'active', 'revoked'))
            );

            create index if not exists idx_business_session_mfa_challenges_active
              on business_session_mfa_challenges (user_id, factor, expires_at desc)
              where consumed_at is null;

            create index if not exists idx_account_mfa_totp_enrollments_active
              on account_mfa_totp_enrollments (user_id, status, created_at desc)
              where status in ('pending', 'active');
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _schemaEnsured = true;
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

        var normalizedLogin = login.Trim().ToLowerInvariant();

        // Lockout gate runs before password verification — see same
        // pattern in PostgresSysAdminAuthRepository for the rationale.
        var lockoutCheck = await lockoutPolicy.CheckAsync(
            LoginLockoutRealms.Business, normalizedLogin, cancellationToken);
        if (lockoutCheck.IsBlocked)
        {
            return Failed(
                lockoutCheck.BlockKind == LoginLockoutKinds.Permanent
                    ? "account_permanently_locked"
                    : "account_temporarily_locked",
                lockoutCheck.Message ?? "Account is locked.");
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var account = await ReadAccountByLoginAsync(connection, transaction, normalizedLogin, cancellationToken);
        if (account is null || !passwordHasher.VerifyPassword(password, account.PasswordHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            await lockoutPolicy.RecordAttemptAsync(
                new LoginAttempt(
                    Realm: LoginLockoutRealms.Business,
                    Email: normalizedLogin,
                    AccountId: account?.Id,
                    RemoteIp: remoteIp,
                    UserAgent: userAgent,
                    Succeeded: false),
                cancellationToken);
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

        var normalizedMfaMode = NormalizeMfaMode(account.MfaMode);
        if (string.Equals(normalizedMfaMode, EmailCodeMfaMode, StringComparison.Ordinal))
        {
            var blockingReason = await GetMfaBlockingReasonAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(blockingReason))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed("mfa_not_ready", blockingReason);
            }

            await InvalidateActiveMfaChallengesAsync(connection, transaction, account.Id, cancellationToken);

            var challengeId = Guid.NewGuid();
            var verificationCode = CreateVerificationCode();
            var verificationCodeHash = HashVerificationCode(verificationCode);
            var challengeExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10);

            await InsertMfaChallengeAsync(
                connection,
                transaction,
                challengeId,
                account.Id,
                account.Email,
                membership,
                verificationCodeHash,
                EmailCodeMfaMode,
                account.SecurityStamp,
                challengeExpiresAtUtc,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var sendResult = await notificationSender.SendVerificationAsync(
                new PlatformVerificationNotificationMessage
                {
                    DispatchId = challengeId,
                    UserId = account.Id,
                    Purpose = "business_sign_in_mfa",
                    Destination = account.Email,
                    RecipientDisplayName = account.DisplayName,
                    VerificationCode = verificationCode,
                    ExpiresAtUtc = challengeExpiresAtUtc
                },
                cancellationToken);

            if (!sendResult.Succeeded)
            {
                await DeleteMfaChallengeAsync(challengeId, cancellationToken);
                return Failed("mfa_delivery_failed", "Second-factor delivery could not be completed.");
            }

            return new PlatformBusinessSessionResult
            {
                Succeeded = true,
                UserId = account.Id,
                ActiveCompanyId = membership.CompanyId,
                AuthenticationStage = "challenge_required",
                RequiresSecondFactor = true,
                MfaChallengeId = challengeId,
                MfaChallengeExpiresAtUtc = challengeExpiresAtUtc,
                AvailableSecondFactors = [EmailCodeMfaMode]
            };
        }

        if (string.Equals(normalizedMfaMode, TotpAppMfaMode, StringComparison.Ordinal))
        {
            var activeEnrollment = await ReadActiveTotpEnrollmentAsync(connection, transaction, account.Id, cancellationToken);
            if (activeEnrollment is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed("mfa_not_ready", "Authenticator-app MFA is configured, but no active TOTP enrollment is available.");
            }

            await InvalidateActiveMfaChallengesAsync(connection, transaction, account.Id, cancellationToken);

            var challengeId = Guid.NewGuid();
            var challengeExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);

            await InsertMfaChallengeAsync(
                connection,
                transaction,
                challengeId,
                account.Id,
                "authenticator_app",
                membership,
                codeHash: string.Empty,
                factor: TotpAppMfaMode,
                securityStampSnapshot: account.SecurityStamp,
                expiresAtUtc: challengeExpiresAtUtc,
                cancellationToken: cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new PlatformBusinessSessionResult
            {
                Succeeded = true,
                UserId = account.Id,
                ActiveCompanyId = membership.CompanyId,
                AuthenticationStage = "challenge_required",
                RequiresSecondFactor = true,
                MfaChallengeId = challengeId,
                MfaChallengeExpiresAtUtc = challengeExpiresAtUtc,
                AvailableSecondFactors = [TotpAppMfaMode]
            };
        }

        var sessionToken = CreateSessionToken();
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(sessionLifetime);

        await InsertSessionAsync(
            connection,
            transaction,
            HashSessionToken(sessionToken),
            account.Id,
            membership,
            account.SecurityStamp,
            expiresAtUtc,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        await lockoutPolicy.RecordAttemptAsync(
            new LoginAttempt(
                Realm: LoginLockoutRealms.Business,
                Email: normalizedLogin,
                AccountId: account.Id,
                RemoteIp: remoteIp,
                UserAgent: userAgent,
                Succeeded: true),
            cancellationToken);

        return new PlatformBusinessSessionResult
        {
            Succeeded = true,
            SessionToken = sessionToken,
            UserId = account.Id,
            ActiveCompanyId = membership.CompanyId,
            ExpiresAtUtc = expiresAtUtc,
            AuthenticationStage = "authenticated"
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

        if (!string.Equals(session.SecurityStampSnapshot, session.CurrentSecurityStamp, StringComparison.Ordinal))
        {
            await DeleteSessionAsync(connection, transaction, session.SessionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Failed("expired_session", "Business session was revoked after account security settings changed.");
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
            ExpiresAtUtc = session.ExpiresAtUtc,
            AuthenticationStage = "authenticated"
        };
    }

    public async Task<PlatformBusinessSessionResult> SwitchActiveCompanyAsync(
        string sessionToken,
        CompanyId activeCompanyId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return Failed("missing_session", "Business session token is required.");
        }

        if (activeCompanyId.Value is null)
        {
            return Failed("invalid_company", "Active company id is required.");
        }

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

        if (!string.Equals(session.SecurityStampSnapshot, session.CurrentSecurityStamp, StringComparison.Ordinal))
        {
            await DeleteSessionAsync(connection, transaction, session.SessionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Failed("expired_session", "Business session was revoked after account security settings changed.");
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
            ExpiresAtUtc = session.ExpiresAtUtc,
            AuthenticationStage = "authenticated"
        };
    }

    public async Task<PlatformBusinessSessionResult> CompleteSecondFactorAsync(
        Guid challengeId,
        string verificationCode,
        TimeSpan sessionLifetime,
        string? remoteIp,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (challengeId == Guid.Empty || string.IsNullOrWhiteSpace(verificationCode))
        {
            return Failed("invalid_mfa_challenge", "MFA challenge id and verification code are required.");
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var challenge = await ReadMfaChallengeAsync(connection, transaction, challengeId, cancellationToken);
        if (challenge is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("invalid_mfa_challenge", "Second-factor challenge was not found.");
        }

        if (!string.Equals(challenge.AccountStatus, "active", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("account_not_active", $"Platform account is {challenge.AccountStatus}.");
        }

        if (!string.Equals(challenge.SecurityStampSnapshot, challenge.CurrentSecurityStamp, StringComparison.Ordinal))
        {
            await InvalidateActiveMfaChallengesAsync(connection, transaction, challenge.UserId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Failed("invalid_mfa_challenge", "Second-factor challenge is no longer valid because account security settings changed. Sign in again.");
        }

        if (challenge.LockedUntilUtc.HasValue && challenge.LockedUntilUtc.Value > DateTimeOffset.UtcNow)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("account_locked", "Platform account is locked.");
        }

        if (challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            await ConsumeMfaChallengeAsync(connection, transaction, challenge.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Failed("expired_mfa_challenge", "Second-factor challenge has expired.");
        }

        var verificationSucceeded = string.Equals(challenge.Factor, TotpAppMfaMode, StringComparison.Ordinal)
            ? await VerifyTotpChallengeAsync(connection, transaction, challenge.UserId, verificationCode, cancellationToken)
            : string.Equals(HashVerificationCode(verificationCode), challenge.CodeHash, StringComparison.Ordinal);

        if (!verificationSucceeded)
        {
            var failedAttempts = await IncrementMfaChallengeFailuresAsync(connection, transaction, challenge.Id, cancellationToken);
            if (failedAttempts >= MaxMfaChallengeFailures)
            {
                var lockedUntilUtc = DateTimeOffset.UtcNow.Add(MfaChallengeLockoutDuration);
                await LockAccountForMfaFailuresAsync(connection, transaction, challenge.UserId, lockedUntilUtc, cancellationToken);
                await InvalidateActiveMfaChallengesAsync(connection, transaction, challenge.UserId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Failed(
                    "account_locked",
                    $"Too many invalid MFA attempts. Platform account is temporarily locked until {lockedUntilUtc:yyyy-MM-dd HH:mm:ss 'UTC'}.");
            }

            await transaction.CommitAsync(cancellationToken);
            return Failed("invalid_mfa_code", "Verification code is invalid.");
        }

        var membership = await ResolveMembershipContextAsync(
            connection,
            transaction,
            challenge.UserId,
            challenge.ActiveCompanyId,
            cancellationToken);
        if (membership is null)
        {
            await ConsumeMfaChallengeAsync(connection, transaction, challenge.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Failed("no_company_access", "This platform account does not currently have access to an active business company.");
        }

        var sessionToken = CreateSessionToken();
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(sessionLifetime);

        await InsertSessionAsync(
            connection,
            transaction,
            HashSessionToken(sessionToken),
            challenge.UserId,
            membership,
            challenge.CurrentSecurityStamp,
            expiresAtUtc,
            cancellationToken);

        await ConsumeMfaChallengeAsync(connection, transaction, challenge.Id, cancellationToken);
        if (string.Equals(challenge.Factor, TotpAppMfaMode, StringComparison.Ordinal))
        {
            await MarkTotpEnrollmentUsedAsync(connection, transaction, challenge.UserId, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new PlatformBusinessSessionResult
        {
            Succeeded = true,
            SessionToken = sessionToken,
            UserId = challenge.UserId,
            ActiveCompanyId = membership.CompanyId,
            ExpiresAtUtc = expiresAtUtc,
            AuthenticationStage = "authenticated"
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
                   coalesce(nullif(display_name, ''), nullif(username, ''), email) as display_name,
                   password_hash,
                   status,
                   locked_until,
                   mfa_mode,
                   security_stamp
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
            UserId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("username"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("username")).Trim(),
            reader.GetString(reader.GetOrdinal("display_name")).Trim(),
            reader.GetString(reader.GetOrdinal("password_hash")).Trim(),
            reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant(),
            reader.IsDBNull(reader.GetOrdinal("locked_until"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("locked_until")),
            reader.GetString(reader.GetOrdinal("mfa_mode")).Trim().ToLowerInvariant(),
            reader.GetString(reader.GetOrdinal("security_stamp")).Trim());
    }

    private static async Task<CompanyId?> ReadPreferredActiveCompanyIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
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
        command.Parameters.AddWithValue("user_id", userId.Value);

        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            string value => CompanyId.Parse(value),
            _ => null
        };
    }

    private static async Task<MembershipContextRecord?> ResolveMembershipContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
        CompanyId? preferredCompanyId,
        CancellationToken cancellationToken)
    {
        if (preferredCompanyId.HasValue && preferredCompanyId.Value.Value is not null)
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
        command.Parameters.AddWithValue("user_id", userId.Value);

        return await ReadMembershipContextAsync(command, cancellationToken);
    }

    private static async Task<MembershipContextRecord?> ReadMembershipContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
        CompanyId companyId,
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
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("company_id", companyId.Value);

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
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
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
        UserId userId,
        MembershipContextRecord membership,
        string securityStampSnapshot,
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
              security_stamp_snapshot,
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
              @security_stamp_snapshot,
              @expires_at
            );
            """;
        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("active_company_id", membership.CompanyId.Value);
        command.Parameters.AddWithValue("membership_id", membership.MembershipId);
        command.Parameters.AddWithValue("role", membership.Role);
        command.Parameters.AddWithValue("permissions", membership.PermissionsJson);
        command.Parameters.AddWithValue("company_status", membership.CompanyStatus);
        command.Parameters.AddWithValue("security_stamp_snapshot", securityStampSnapshot);
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
        command.Parameters.AddWithValue("active_company_id", membership.CompanyId.Value);
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
                   s.security_stamp_snapshot,
                   u.status as account_status,
                   u.locked_until,
                   u.security_stamp
            from business_sessions s
            inner join users u on u.id = s.user_id
            where s.token_hash = @token_hash
              and s.revoked_at is null
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
            UserId.Parse(reader.GetString(reader.GetOrdinal("user_id"))),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("active_company_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("expires_at")),
            reader.GetString(reader.GetOrdinal("security_stamp_snapshot")).Trim(),
            reader.GetString(reader.GetOrdinal("account_status")).Trim().ToLowerInvariant(),
            reader.IsDBNull(reader.GetOrdinal("locked_until"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("locked_until")),
            reader.GetString(reader.GetOrdinal("security_stamp")).Trim());
    }

    private async Task<string?> GetMfaBlockingReasonAsync(CancellationToken cancellationToken)
    {
        var readiness = await runtimeStateRepository.GetNotificationReadinessStateAsync(cancellationToken);
        if (readiness is null || !readiness.VerificationReady)
        {
            return "Second-factor delivery is not ready for business sign-in.";
        }

        var configurationError = notificationSender.GetConfigurationError();
        return string.IsNullOrWhiteSpace(configurationError)
            ? null
            : "Second-factor delivery is not ready for business sign-in.";
    }

    private static string NormalizeMfaMode(string? mfaMode) =>
        string.IsNullOrWhiteSpace(mfaMode)
            ? NoMfaMode
            : mfaMode.Trim().ToLowerInvariant();

    private static async Task InsertMfaChallengeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid challengeId,
        UserId userId,
        string destination,
        MembershipContextRecord membership,
        string codeHash,
        string factor,
        string securityStampSnapshot,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into business_session_mfa_challenges (
              id,
              user_id,
              active_company_id,
              membership_id,
              role,
              permissions,
              company_status,
              factor,
              destination,
              code_hash,
              security_stamp_snapshot,
              expires_at
            )
            values (
              @id,
              @user_id,
              @active_company_id,
              @membership_id,
              @role,
              @permissions::jsonb,
              @company_status,
              @factor,
              @destination,
              @code_hash,
              @security_stamp_snapshot,
              @expires_at
            );
            """;
        command.Parameters.AddWithValue("id", challengeId);
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("active_company_id", membership.CompanyId.Value);
        command.Parameters.AddWithValue("membership_id", membership.MembershipId);
        command.Parameters.AddWithValue("role", membership.Role);
        command.Parameters.AddWithValue("permissions", membership.PermissionsJson);
        command.Parameters.AddWithValue("company_status", membership.CompanyStatus);
        command.Parameters.AddWithValue("factor", factor);
        command.Parameters.AddWithValue("destination", destination);
        command.Parameters.AddWithValue("code_hash", codeHash);
        command.Parameters.AddWithValue("security_stamp_snapshot", securityStampSnapshot);
        command.Parameters.AddWithValue("expires_at", expiresAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> VerifyTotpChallengeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        var enrollment = await ReadActiveTotpEnrollmentAsync(connection, transaction, userId, cancellationToken);
        return enrollment is not null &&
               PlatformTotpAuthenticator.VerifyCode(enrollment.SecretBase32, verificationCode, DateTimeOffset.UtcNow);
    }

    private async Task<TotpEnrollmentRecord?> ReadActiveTotpEnrollmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, secret_base32
            from account_mfa_totp_enrollments
            where user_id = @user_id
              and status = @status
              and revoked_at is null
            order by confirmed_at desc nulls last, created_at desc
            limit 1
            for update;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("status", "active");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TotpEnrollmentRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            totpSecretProtector.Unprotect(reader.GetString(reader.GetOrdinal("secret_base32")).Trim()));
    }

    private static async Task MarkTotpEnrollmentUsedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update account_mfa_totp_enrollments
            set last_used_at = now()
            where user_id = @user_id
              and status = 'active'
              and revoked_at is null;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InvalidateActiveMfaChallengesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update business_session_mfa_challenges
            set consumed_at = now()
            where user_id = @user_id
              and consumed_at is null;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteMfaChallengeAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from business_session_mfa_challenges
            where id = @id;
            """;
        command.Parameters.AddWithValue("id", challengeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<MfaChallengeRecord?> ReadMfaChallengeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid challengeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select c.id,
                   c.user_id,
                   c.active_company_id,
                   c.factor,
                   c.code_hash,
                   c.failed_attempts,
                   c.security_stamp_snapshot,
                   c.expires_at,
                   u.status as account_status,
                   u.locked_until,
                   u.security_stamp
            from business_session_mfa_challenges c
            inner join users u on u.id = c.user_id
            where c.id = @id
              and c.consumed_at is null
            limit 1
            for update;
            """;
        command.Parameters.AddWithValue("id", challengeId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MfaChallengeRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            UserId.Parse(reader.GetString(reader.GetOrdinal("user_id"))),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("active_company_id"))),
            reader.GetString(reader.GetOrdinal("factor")).Trim().ToLowerInvariant(),
            reader.GetString(reader.GetOrdinal("code_hash")).Trim(),
            reader.GetInt32(reader.GetOrdinal("failed_attempts")),
            reader.GetString(reader.GetOrdinal("security_stamp_snapshot")).Trim(),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("expires_at")),
            reader.GetString(reader.GetOrdinal("account_status")).Trim().ToLowerInvariant(),
            reader.IsDBNull(reader.GetOrdinal("locked_until"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("locked_until")),
            reader.GetString(reader.GetOrdinal("security_stamp")).Trim());
    }

    private static async Task<int> IncrementMfaChallengeFailuresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid challengeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update business_session_mfa_challenges
            set failed_attempts = failed_attempts + 1
            where id = @id
            returning failed_attempts;
            """;
        command.Parameters.AddWithValue("id", challengeId);
        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            int value => value,
            _ => 0
        };
    }

    private static async Task LockAccountForMfaFailuresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
        DateTimeOffset lockedUntilUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update users
            set locked_until = greatest(coalesce(locked_until, '-infinity'::timestamptz), @locked_until)
            where id = @user_id;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("locked_until", lockedUntilUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ConsumeMfaChallengeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid challengeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update business_session_mfa_challenges
            set consumed_at = now()
            where id = @id
              and consumed_at is null;
            """;
        command.Parameters.AddWithValue("id", challengeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateVerificationCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> randomBytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(randomBytes);

        Span<char> code = stackalloc char[6];
        for (var index = 0; index < code.Length; index++)
        {
            code[index] = alphabet[randomBytes[index] % alphabet.Length];
        }

        return new string(code);
    }

    private static string HashVerificationCode(string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash);
    }

    private sealed record AccountRecord(
        UserId Id,
        string Email,
        string Username,
        string DisplayName,
        string PasswordHash,
        string Status,
        DateTimeOffset? LockedUntilUtc,
        string MfaMode,
        string SecurityStamp);

    private sealed record MembershipContextRecord(
        Guid MembershipId,
        CompanyId CompanyId,
        string Role,
        string PermissionsJson,
        string CompanyStatus);

    private sealed record SessionRecord(
        Guid SessionId,
        UserId UserId,
        CompanyId ActiveCompanyId,
        DateTimeOffset ExpiresAtUtc,
        string SecurityStampSnapshot,
        string AccountStatus,
        DateTimeOffset? LockedUntilUtc,
        string CurrentSecurityStamp);

    private sealed record MfaChallengeRecord(
        Guid Id,
        UserId UserId,
        CompanyId ActiveCompanyId,
        string Factor,
        string CodeHash,
        int FailedAttempts,
        string SecurityStampSnapshot,
        DateTimeOffset ExpiresAtUtc,
        string AccountStatus,
        DateTimeOffset? LockedUntilUtc,
        string CurrentSecurityStamp);

    private sealed record TotpEnrollmentRecord(
        Guid Id,
        string SecretBase32);
}
