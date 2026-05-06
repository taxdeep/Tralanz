using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Citus.Platform.Infrastructure.Notifications;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PostgresPlatformGovernanceRepository(
    PlatformPostgresConnectionFactory connectionFactory,
    IPlatformRuntimeStateRepository runtimeStateRepository,
    IPlatformVerificationNotificationSender notificationSender) : IPlatformGovernanceRepository
{
    private static readonly string[] CompanyStatuses = ["active", "inactive", "suspended", "archived"];
    private static readonly string[] AccountStatuses = ["active", "disabled", "locked", "pending_verification"];
    private static readonly string[] MfaRecoveryStatuses = ["requested", "approved", "rejected", "executed"];
    private static readonly string[] ReviewableAuditActions =
    [
        "company_status_changed",
        "account_status_changed",
        "account_totp_enrollment_started",
        "account_totp_enrollment_confirmed",
        "account_mfa_recovery_requested",
        "account_mfa_recovery_approved",
        "account_mfa_recovery_rejected",
        "account_mfa_recovery_executed",
        "account_mfa_reset",
        "profile_display_name_saved",
        "email_change_requested",
        "email_change_dispatched",
        "email_change_dispatch_failed",
        "email_change_confirmed",
        "password_change_requested",
        "password_change_dispatched",
        "password_change_dispatch_failed",
        "password_change_confirmed",
        "password_reset_requested",
        "password_reset_dispatched",
        "password_reset_dispatch_failed",
        "membership_role_changed",
        "membership_permissions_saved",
        "sysadmin_first_account_created",
        "sysadmin_password_rotated"
    ];

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create extension if not exists pgcrypto;

            alter table users
              add column if not exists display_name text;

            alter table users
              add column if not exists status text not null default 'active';

            alter table users
              add column if not exists email_verified_at timestamptz;

            alter table users
              add column if not exists locked_until timestamptz;

            alter table users
              add column if not exists security_stamp text not null default gen_random_uuid()::text;

            alter table users
              add column if not exists mfa_mode text not null default 'none';

            create table if not exists sysadmin_accounts (
              id uuid primary key default gen_random_uuid(),
              email text not null unique,
              display_name text not null default '',
              password_hash text not null,
              status text not null default 'active',
              last_login_at timestamptz,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create table if not exists account_mfa_recovery_requests (
              id uuid primary key default gen_random_uuid(),
              user_id uuid not null references users(id) on delete cascade,
              requested_by_user_id uuid not null references users(id) on delete cascade,
              current_mfa_mode text not null,
              status text not null default 'requested',
              request_reason text not null,
              requested_at timestamptz not null default now(),
              review_reason text,
              reviewed_at timestamptz,
              reviewed_by_sysadmin_account_id uuid references sysadmin_accounts(id) on delete set null,
              execution_reason text,
              executed_at timestamptz,
              executed_by_sysadmin_account_id uuid references sysadmin_accounts(id) on delete set null,
              constraint account_mfa_recovery_requests_status_chk check (status in ('requested', 'approved', 'rejected', 'executed'))
            );

            alter table account_mfa_recovery_requests
              drop constraint if exists account_mfa_recovery_requests_current_mode_chk;

            alter table account_mfa_recovery_requests
              add constraint account_mfa_recovery_requests_current_mode_chk
              check (current_mfa_mode in ('none', 'email_code', 'totp_app'));

            create table if not exists business_session_mfa_challenges (
              id uuid primary key default gen_random_uuid(),
              user_id uuid not null references users(id) on delete cascade,
              active_company_id uuid not null,
              membership_id uuid not null,
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
              user_id uuid not null references users(id) on delete cascade,
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

            create table if not exists account_verification_codes (
              id uuid primary key default gen_random_uuid(),
              user_id uuid not null references users(id) on delete cascade,
              purpose text not null,
              destination text,
              code_hash text not null,
              expires_at timestamptz not null,
              consumed_at timestamptz,
              failed_attempts integer not null default 0,
              created_at timestamptz not null default now(),
              payload jsonb not null default '{}'::jsonb
            );

            alter table account_verification_codes
              add column if not exists payload jsonb not null default '{}'::jsonb;

            create table if not exists platform_notification_dispatches (
              id uuid primary key default gen_random_uuid(),
              notification_type text not null,
              destination text not null,
              status text not null default 'queued',
              provider_key text,
              attempt_count integer not null default 0,
              sent_at timestamptz,
              failed_at timestamptz,
              last_error text,
              payload jsonb not null default '{}'::jsonb,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            alter table platform_notification_dispatches
              add column if not exists provider_key text;

            alter table platform_notification_dispatches
              add column if not exists attempt_count integer not null default 0;

            alter table platform_notification_dispatches
              add column if not exists sent_at timestamptz;

            alter table platform_notification_dispatches
              add column if not exists failed_at timestamptz;

            alter table platform_notification_dispatches
              add column if not exists last_error text;

            create table if not exists audit_logs (
              id uuid primary key default gen_random_uuid(),
              company_id uuid null,
              actor_type text not null,
              actor_id uuid null,
              entity_type text not null,
              entity_id uuid not null,
              action text not null,
              payload jsonb not null default '{}'::jsonb,
              created_at timestamptz not null default now()
            );

            create index if not exists idx_users_status_email
              on users (status, email);

            create index if not exists idx_account_verification_codes_active
              on account_verification_codes (user_id, purpose, expires_at desc)
              where consumed_at is null;

            create index if not exists idx_account_mfa_recovery_requests_open
              on account_mfa_recovery_requests (status, requested_at desc)
              where status in ('requested', 'approved');

            create index if not exists idx_business_session_mfa_challenges_active
              on business_session_mfa_challenges (user_id, factor, expires_at desc)
              where consumed_at is null;

            create index if not exists idx_platform_notification_dispatches_status
              on platform_notification_dispatches (status, created_at desc);

            create index if not exists idx_audit_logs_action_created_at
              on audit_logs (action, created_at desc);
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlatformAuditEvent>> ListRecentAuditEventsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        var normalizedLimit = Math.Clamp(limit, 1, 200);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              al.id,
              al.company_id,
              coalesce(c.entity_number, '') as company_entity_number,
              coalesce(c.legal_name, '') as company_legal_name,
              al.actor_type,
              al.actor_id,
              coalesce(sys_actor.display_name, '') as sysadmin_display_name,
              coalesce(sys_actor.email, '') as sysadmin_email,
              coalesce(user_actor.username, '') as actor_username,
              coalesce(user_actor.email, '') as actor_email,
              al.entity_type,
              al.entity_id,
              coalesce(entity_user.username, '') as entity_username,
              coalesce(entity_user.email, '') as entity_email,
              coalesce(entity_sysadmin.display_name, '') as entity_sysadmin_display_name,
              coalesce(entity_sysadmin.email, '') as entity_sysadmin_email,
              coalesce(membership_user.username, '') as membership_username,
              coalesce(membership_user.email, '') as membership_email,
              al.action,
              al.payload::text as payload,
              al.created_at
            from audit_logs al
            left join companies c on c.id = al.company_id
            left join sysadmin_accounts sys_actor
              on sys_actor.id = al.actor_id
             and al.actor_type = 'sysadmin'
            left join users user_actor
              on user_actor.id = al.actor_id
             and al.actor_type = 'user'
            left join users entity_user
              on entity_user.id = al.entity_id
             and al.entity_type = 'platform_account'
            left join sysadmin_accounts entity_sysadmin
              on entity_sysadmin.id = al.entity_id
             and al.entity_type = 'sysadmin_account'
            left join company_memberships membership
              on membership.id = al.entity_id
             and al.entity_type = 'company_membership'
            left join users membership_user on membership_user.id = membership.user_id
            where al.action = any(@actions)
            order by al.created_at desc
            limit @limit;
            """;
        command.Parameters.AddWithValue("actions", ReviewableAuditActions);
        command.Parameters.AddWithValue("limit", normalizedLimit);

        var events = new List<PlatformAuditEvent>(normalizedLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadAuditEvent(reader));
        }

        return events;
    }

    public async Task<IReadOnlyList<PlatformAuditEvent>> ListAccountMfaTimelineAsync(
        UserId accountId,
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        var normalizedLimit = Math.Clamp(limit, 1, 50);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              al.id,
              al.company_id,
              coalesce(c.entity_number, '') as company_entity_number,
              coalesce(c.legal_name, '') as company_legal_name,
              al.actor_type,
              al.actor_id,
              coalesce(sys_actor.display_name, '') as sysadmin_display_name,
              coalesce(sys_actor.email, '') as sysadmin_email,
              coalesce(user_actor.username, '') as actor_username,
              coalesce(user_actor.email, '') as actor_email,
              al.entity_type,
              al.entity_id,
              coalesce(entity_user.username, '') as entity_username,
              coalesce(entity_user.email, '') as entity_email,
              coalesce(entity_sysadmin.display_name, '') as entity_sysadmin_display_name,
              coalesce(entity_sysadmin.email, '') as entity_sysadmin_email,
              coalesce(membership_user.username, '') as membership_username,
              coalesce(membership_user.email, '') as membership_email,
              al.action,
              al.payload::text as payload,
              al.created_at
            from audit_logs al
            left join companies c on c.id = al.company_id
            left join sysadmin_accounts sys_actor
              on sys_actor.id = al.actor_id
             and al.actor_type = 'sysadmin'
            left join users user_actor
              on user_actor.id = al.actor_id
             and al.actor_type = 'user'
            left join users entity_user
              on entity_user.id = al.entity_id
             and al.entity_type = 'platform_account'
            left join sysadmin_accounts entity_sysadmin
              on entity_sysadmin.id = al.entity_id
             and al.entity_type = 'sysadmin_account'
            left join company_memberships membership
              on membership.id = al.entity_id
             and al.entity_type = 'company_membership'
            left join users membership_user on membership_user.id = membership.user_id
            where al.entity_type = 'platform_account'
              and al.entity_id = @account_id
              and al.action = any(@actions)
            order by al.created_at desc
            limit @limit;
            """;
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue(
            "actions",
            new[]
            {
                "profile_mfa_mode_saved",
                "account_mfa_recovery_requested",
                "account_mfa_recovery_approved",
                "account_mfa_recovery_rejected",
                "account_mfa_recovery_executed",
                "account_mfa_reset"
            });
        command.Parameters.AddWithValue("limit", normalizedLimit);

        var events = new List<PlatformAuditEvent>(normalizedLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadAuditEvent(reader));
        }

        return events;
    }

    public async Task<IReadOnlyList<ManagedPlatformAccountSummary>> ListManagedUsersAsync(
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              u.id,
              coalesce(nullif(u.display_name, ''), nullif(u.username, ''), u.email) as display_name,
              u.email,
              coalesce(nullif(u.username, ''), u.email) as username,
              u.status,
              u.mfa_mode,
              coalesce(mfa_recovery.status, '') as active_mfa_recovery_status,
              coalesce(memberships.company_codes, '') as company_codes,
              mfa_reset.created_at as last_mfa_reset_at_utc,
              coalesce(mfa_reset.reason, '') as last_mfa_reset_reason
            from users u
            left join lateral (
              select string_agg(distinct coalesce(c.entity_number, ''), ',' order by coalesce(c.entity_number, '')) as company_codes
              from company_memberships m
              left join companies c on c.id = m.company_id
              where m.user_id = u.id
                and m.is_active = true
            ) memberships on true
            left join lateral (
              select status
              from account_mfa_recovery_requests
              where user_id = u.id
                and status in ('requested', 'approved')
              order by requested_at desc
              limit 1
            ) mfa_recovery on true
            left join lateral (
              select
                created_at,
                coalesce(payload ->> 'reason', '') as reason
              from audit_logs
              where entity_type = 'platform_account'
                and entity_id = u.id
                and action = 'account_mfa_reset'
              order by created_at desc
              limit 1
            ) mfa_reset on true
            order by
              coalesce(nullif(u.display_name, ''), nullif(u.username, ''), u.email),
              u.email;
            """;

        var users = new List<ManagedPlatformAccountSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(ReadManagedUserSummary(reader));
        }

        return users;
    }

    public async Task<IReadOnlyList<MfaRecoveryRequestSummary>> ListOpenMfaRecoveryRequestsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              r.id,
              r.user_id,
              coalesce(nullif(u.display_name, ''), nullif(u.username, ''), u.email) as display_name,
              u.email,
              coalesce(nullif(u.username, ''), u.email) as username,
              r.current_mfa_mode,
              r.status,
              r.request_reason,
              r.requested_at,
              coalesce(r.review_reason, '') as review_reason,
              r.reviewed_at,
              coalesce(sa.display_name, sa.email, '') as reviewed_by_display_name
            from account_mfa_recovery_requests r
            inner join users u on u.id = r.user_id
            left join sysadmin_accounts sa on sa.id = r.reviewed_by_sysadmin_account_id
            where r.status in ('requested', 'approved')
            order by
              case when r.status = 'requested' then 0 else 1 end,
              r.requested_at desc;
            """;

        var requests = new List<MfaRecoveryRequestSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new MfaRecoveryRequestSummary
            {
                RequestId = reader.GetGuid(reader.GetOrdinal("id")),
                AccountId = UserId.Parse(reader.GetString(reader.GetOrdinal("user_id"))),
                DisplayName = reader.GetString(reader.GetOrdinal("display_name")).Trim(),
                Email = reader.GetString(reader.GetOrdinal("email")).Trim(),
                Username = reader.GetString(reader.GetOrdinal("username")).Trim(),
                CurrentMfaMode = NormalizeMfaMode(reader.GetString(reader.GetOrdinal("current_mfa_mode"))),
                Status = reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant(),
                RequestReason = reader.GetString(reader.GetOrdinal("request_reason")).Trim(),
                RequestedAtUtc = CoerceTimestamp(reader.GetValue(reader.GetOrdinal("requested_at"))),
                ReviewReason = reader.GetString(reader.GetOrdinal("review_reason")).Trim(),
                ReviewedAtUtc = reader.IsDBNull(reader.GetOrdinal("reviewed_at"))
                    ? null
                    : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("reviewed_at"))),
                ReviewedByDisplayName = reader.GetString(reader.GetOrdinal("reviewed_by_display_name")).Trim()
            });
        }

        return requests;
    }

    public async Task<IReadOnlyList<MfaRecoveryRequestSummary>> ListAccountMfaRecoveryHistoryAsync(
        UserId accountId,
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        var normalizedLimit = Math.Clamp(limit, 1, 50);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              r.id,
              r.user_id,
              coalesce(nullif(u.display_name, ''), nullif(u.username, ''), u.email) as display_name,
              u.email,
              coalesce(nullif(u.username, ''), u.email) as username,
              r.current_mfa_mode,
              r.status,
              r.request_reason,
              r.requested_at,
              coalesce(r.review_reason, '') as review_reason,
              r.reviewed_at,
              coalesce(review_sa.display_name, review_sa.email, '') as reviewed_by_display_name,
              coalesce(r.execution_reason, '') as execution_reason,
              r.executed_at,
              coalesce(execute_sa.display_name, execute_sa.email, '') as executed_by_display_name
            from account_mfa_recovery_requests r
            inner join users u on u.id = r.user_id
            left join sysadmin_accounts review_sa on review_sa.id = r.reviewed_by_sysadmin_account_id
            left join sysadmin_accounts execute_sa on execute_sa.id = r.executed_by_sysadmin_account_id
            where r.user_id = @account_id
            order by r.requested_at desc
            limit @limit;
            """;
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("limit", normalizedLimit);

        var requests = new List<MfaRecoveryRequestSummary>(normalizedLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new MfaRecoveryRequestSummary
            {
                RequestId = reader.GetGuid(reader.GetOrdinal("id")),
                AccountId = UserId.Parse(reader.GetString(reader.GetOrdinal("user_id"))),
                DisplayName = reader.GetString(reader.GetOrdinal("display_name")).Trim(),
                Email = reader.GetString(reader.GetOrdinal("email")).Trim(),
                Username = reader.GetString(reader.GetOrdinal("username")).Trim(),
                CurrentMfaMode = NormalizeMfaMode(reader.GetString(reader.GetOrdinal("current_mfa_mode"))),
                Status = reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant(),
                RequestReason = reader.GetString(reader.GetOrdinal("request_reason")).Trim(),
                RequestedAtUtc = CoerceTimestamp(reader.GetValue(reader.GetOrdinal("requested_at"))),
                ReviewReason = reader.GetString(reader.GetOrdinal("review_reason")).Trim(),
                ReviewedAtUtc = reader.IsDBNull(reader.GetOrdinal("reviewed_at"))
                    ? null
                    : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("reviewed_at"))),
                ReviewedByDisplayName = reader.GetString(reader.GetOrdinal("reviewed_by_display_name")).Trim(),
                ExecutionReason = reader.GetString(reader.GetOrdinal("execution_reason")).Trim(),
                ExecutedAtUtc = reader.IsDBNull(reader.GetOrdinal("executed_at"))
                    ? null
                    : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("executed_at"))),
                ExecutedByDisplayName = reader.GetString(reader.GetOrdinal("executed_by_display_name")).Trim()
            });
        }

        return requests;
    }

    public async Task<CompanyStatusGovernanceResult?> SetCompanyStatusAsync(
        CompanyId companyId,
        string status,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = NormalizeRequired(status, "Company status");
        EnsureAllowed(normalizedStatus, CompanyStatuses, "company status");
        var normalizedReason = NormalizeReason(reason, "Company status updated by SysAdmin.");

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadCompanyAsync(connection, transaction, companyId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                update companies
                set status = @status,
                    updated_at = now()
                where id = @company_id;
                """;
            update.Parameters.AddWithValue("company_id", companyId.Value);
            update.Parameters.AddWithValue("status", normalizedStatus);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            companyId,
            sysAdminAccountId,
            "company",
            companyId.Value,
            "company_status_changed",
            """
            jsonb_build_object(
              'previous_status', @previous_status,
              'status', @status,
              'reason', @reason
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("previous_status", current.Status);
                command.Parameters.AddWithValue("status", normalizedStatus);
                command.Parameters.AddWithValue("reason", normalizedReason);
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new CompanyStatusGovernanceResult
        {
            CompanyId = companyId,
            EntityNumber = current.EntityNumber,
            LegalName = current.LegalName,
            PreviousStatus = current.Status,
            Status = normalizedStatus,
            Reason = normalizedReason,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public async Task<AccountStatusGovernanceResult?> SetAccountStatusAsync(
        UserId accountId,
        string status,
        DateTimeOffset? lockedUntilUtc,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = NormalizeRequired(status, "Account status");
        EnsureAllowed(normalizedStatus, AccountStatuses, "account status");
        var normalizedReason = NormalizeReason(reason, "Account status updated by SysAdmin.");

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadAccountAsync(connection, transaction, accountId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                update users
                set status = @status,
                    locked_until = @locked_until,
                    security_stamp = gen_random_uuid()::text,
                    updated_at = now()
                where id = @account_id;
                """;
            update.Parameters.AddWithValue("account_id", accountId);
            update.Parameters.AddWithValue("status", normalizedStatus);
            update.Parameters.AddWithValue(
                "locked_until",
                lockedUntilUtc.HasValue ? lockedUntilUtc.Value : DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            companyId: null,
            sysAdminAccountId,
            "platform_account",
            accountId.Value,
            "account_status_changed",
            """
            jsonb_build_object(
              'previous_status', @previous_status,
              'status', @status,
              'locked_until_utc', @locked_until_utc,
              'reason', @reason
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("previous_status", current.Status);
                command.Parameters.AddWithValue("status", normalizedStatus);
                command.Parameters.AddWithValue(
                    "locked_until_utc",
                    lockedUntilUtc.HasValue ? lockedUntilUtc.Value : DBNull.Value);
                command.Parameters.AddWithValue("reason", normalizedReason);
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new AccountStatusGovernanceResult
        {
            AccountId = accountId,
            Email = current.Email,
            Username = current.Username,
            PreviousStatus = current.Status,
            Status = normalizedStatus,
            LockedUntilUtc = lockedUntilUtc,
            Reason = normalizedReason,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public async Task<PasswordResetGovernanceResult?> RequestPasswordResetAsync(
        UserId accountId,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        var normalizedReason = NormalizeReason(reason, "Password reset requested by SysAdmin.");

        await EnsureSchemaAsync(cancellationToken);

        var notificationReadiness = await runtimeStateRepository.GetNotificationReadinessStateAsync(cancellationToken);
        if (notificationReadiness is null || !notificationReadiness.IsVerificationDeliveryReady)
        {
            var blockingReason = notificationReadiness?.GetBlockingReason() ?? "Notification readiness has not been configured.";
            throw new InvalidOperationException(
                $"Password reset is blocked because notification readiness is not verified. {blockingReason}");
        }

        var configurationError = notificationSender.GetConfigurationError();
        if (!string.IsNullOrWhiteSpace(configurationError))
        {
            throw new InvalidOperationException(
                $"Password reset is blocked because the notification provider is not configured. {configurationError}");
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadAccountAsync(connection, transaction, accountId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                update users
                set security_stamp = gen_random_uuid()::text,
                    updated_at = now()
                where id = @account_id;
                """;
            update.Parameters.AddWithValue("account_id", accountId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var verificationCodeId = Guid.NewGuid();
        var verificationCode = CreateVerificationCode();
        var verificationCodeHash = HashVerificationCode(verificationCode);
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15);

        await using (var insertVerificationCode = connection.CreateCommand())
        {
            insertVerificationCode.Transaction = transaction;
            insertVerificationCode.CommandText =
                """
                insert into account_verification_codes (
                  id,
                  user_id,
                  purpose,
                  destination,
                  code_hash,
                  expires_at
                )
                values (
                  @id,
                  @user_id,
                  'password_reset',
                  @destination,
                  @code_hash,
                  @expires_at
                );
                """;
            insertVerificationCode.Parameters.AddWithValue("id", verificationCodeId);
            insertVerificationCode.Parameters.AddWithValue("user_id", accountId);
            insertVerificationCode.Parameters.AddWithValue("destination", current.Email);
            insertVerificationCode.Parameters.AddWithValue("code_hash", verificationCodeHash);
            insertVerificationCode.Parameters.AddWithValue("expires_at", expiresAtUtc);
            await insertVerificationCode.ExecuteNonQueryAsync(cancellationToken);
        }

        var dispatchId = Guid.NewGuid();

        await using (var insertDispatch = connection.CreateCommand())
        {
            insertDispatch.Transaction = transaction;
            insertDispatch.CommandText =
                """
                insert into platform_notification_dispatches (
                  id,
                  notification_type,
                  destination,
                  status,
                  payload
                )
                values (
                  @id,
                  'password_reset_verification',
                  @destination,
                  'queued',
                  jsonb_build_object(
                    'account_id', @account_id,
                    'verification_code_id', @verification_code_id,
                    'purpose', 'password_reset',
                    'masked_destination', @masked_destination,
                    'delivery_mode', 'smtp_provider_pending'
                  )
                );
                """;
            insertDispatch.Parameters.AddWithValue("id", dispatchId);
            insertDispatch.Parameters.AddWithValue("destination", current.Email);
            insertDispatch.Parameters.AddWithValue("account_id", accountId);
            insertDispatch.Parameters.AddWithValue("verification_code_id", verificationCodeId);
            insertDispatch.Parameters.AddWithValue("masked_destination", MaskEmail(current.Email));
            await insertDispatch.ExecuteNonQueryAsync(cancellationToken);
        }

        var requestId = await InsertAuditAsync(
            connection,
            transaction,
            companyId: null,
            sysAdminAccountId,
            "platform_account",
            accountId.Value,
            "password_reset_requested",
            """
            jsonb_build_object(
              'reason', @reason,
              'delivery_status', 'verification_code_issued_dispatch_queued',
              'verification_code_id', @verification_code_id,
              'notification_dispatch_id', @notification_dispatch_id,
              'expires_at_utc', @expires_at_utc,
              'masked_destination', @masked_destination
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("reason", normalizedReason);
                command.Parameters.AddWithValue("verification_code_id", verificationCodeId);
                command.Parameters.AddWithValue("notification_dispatch_id", dispatchId);
                command.Parameters.AddWithValue("expires_at_utc", expiresAtUtc);
                command.Parameters.AddWithValue("masked_destination", MaskEmail(current.Email));
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var sendResult = await notificationSender.SendPasswordResetAsync(
            new PasswordResetNotificationMessage
            {
                DispatchId = dispatchId,
                Destination = current.Email,
                RecipientDisplayName = string.IsNullOrWhiteSpace(current.Username) ? current.Email : current.Username,
                VerificationCode = verificationCode,
                ExpiresAtUtc = expiresAtUtc
            },
            cancellationToken);

        if (sendResult.Succeeded)
        {
            await FinalizeDispatchAsSentAsync(
                connection,
                dispatchId,
                requestId,
                accountId,
                sysAdminAccountId,
                current.Email,
                sendResult,
                cancellationToken);

            return new PasswordResetGovernanceResult
            {
                RequestId = requestId,
                AccountId = accountId,
                Email = current.Email,
                Username = current.Username,
                DeliveryStatus = "verification_code_sent",
                Reason = normalizedReason,
                RequestedAtUtc = DateTimeOffset.UtcNow
            };
        }

        await FinalizeDispatchAsFailedAsync(
            connection,
            dispatchId,
            requestId,
            accountId,
            sysAdminAccountId,
            current.Email,
            sendResult,
            cancellationToken);

        throw new PlatformNotificationDeliveryException(
            $"Password reset delivery failed. {sendResult.FailureMessage}");
    }

    public async Task<AccountMfaResetGovernanceResult?> ResetAccountMfaAsync(
        UserId accountId,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        var normalizedReason = NormalizeReason(reason, "MFA reset by SysAdmin.");

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadAccountAsync(connection, transaction, accountId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var activeRecoveryRequest = await ReadOpenMfaRecoveryRequestForAccountAsync(
            connection,
            transaction,
            accountId,
            cancellationToken);
        if (activeRecoveryRequest is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("MFA reset is blocked because the account has a pending recovery review.");
        }

        var reset = await ResetAccountMfaCoreAsync(
            connection,
            transaction,
            current,
            normalizedReason,
            sysAdminAccountId,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return reset;
    }

    public async Task<MfaRecoveryReviewResult?> ReviewMfaRecoveryRequestAsync(
        Guid requestId,
        string decision,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        var normalizedDecision = NormalizeRecoveryDecision(decision);
        var normalizedReason = NormalizeReason(reason, $"MFA recovery {normalizedDecision} by SysAdmin.");

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var request = await ReadMfaRecoveryRequestAsync(connection, transaction, requestId, cancellationToken);
        if (request is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (!string.Equals(request.Status, "requested", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("MFA recovery request is no longer awaiting review.");
        }

        var reviewedAtUtc = DateTimeOffset.UtcNow;
        var targetStatus = normalizedDecision switch
        {
            "approve" => "approved",
            _ => "rejected"
        };

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update account_mfa_recovery_requests
                set status = @status,
                    review_reason = @review_reason,
                    reviewed_at = @reviewed_at,
                    reviewed_by_sysadmin_account_id = @reviewed_by_sysadmin_account_id
                where id = @id;
                """;
            command.Parameters.AddWithValue("id", requestId);
            command.Parameters.AddWithValue("status", targetStatus);
            command.Parameters.AddWithValue("review_reason", normalizedReason);
            command.Parameters.AddWithValue("reviewed_at", reviewedAtUtc);
            command.Parameters.AddWithValue(
                "reviewed_by_sysadmin_account_id",
                sysAdminAccountId.HasValue ? sysAdminAccountId.Value : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            companyId: null,
            sysAdminAccountId,
            "platform_account",
            request.AccountId.Value,
            targetStatus switch
            {
                "approved" => "account_mfa_recovery_approved",
                _ => "account_mfa_recovery_rejected"
            },
            """
            jsonb_build_object(
              'request_id', @request_id,
              'current_mfa_mode', @current_mfa_mode,
              'status', @status,
              'reason', @reason
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("request_id", requestId);
                command.Parameters.AddWithValue("current_mfa_mode", request.CurrentMfaMode);
                command.Parameters.AddWithValue("status", targetStatus);
                command.Parameters.AddWithValue("reason", normalizedReason);
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new MfaRecoveryReviewResult
        {
            RequestId = requestId,
            AccountId = request.AccountId,
            Status = targetStatus,
            ReviewReason = normalizedReason,
            ReviewedAtUtc = reviewedAtUtc
        };
    }

    public async Task<MfaRecoveryExecutionResult?> ExecuteMfaRecoveryRequestAsync(
        Guid requestId,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        var normalizedReason = NormalizeReason(reason, "Approved MFA recovery executed by SysAdmin.");

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var request = await ReadMfaRecoveryRequestAsync(connection, transaction, requestId, cancellationToken);
        if (request is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (!string.Equals(request.Status, "approved", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("MFA recovery request must be approved before execution.");
        }

        var account = await ReadAccountAsync(connection, transaction, request.AccountId, cancellationToken);
        if (account is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var reset = await ResetAccountMfaCoreAsync(
            connection,
            transaction,
            account,
            normalizedReason,
            sysAdminAccountId,
            cancellationToken);

        var executedAtUtc = DateTimeOffset.UtcNow;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update account_mfa_recovery_requests
                set status = 'executed',
                    execution_reason = @execution_reason,
                    executed_at = @executed_at,
                    executed_by_sysadmin_account_id = @executed_by_sysadmin_account_id
                where id = @id;
                """;
            command.Parameters.AddWithValue("id", requestId);
            command.Parameters.AddWithValue("execution_reason", normalizedReason);
            command.Parameters.AddWithValue("executed_at", executedAtUtc);
            command.Parameters.AddWithValue(
                "executed_by_sysadmin_account_id",
                sysAdminAccountId.HasValue ? sysAdminAccountId.Value : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            companyId: null,
            sysAdminAccountId,
            "platform_account",
            request.AccountId.Value,
            "account_mfa_recovery_executed",
            """
            jsonb_build_object(
              'request_id', @request_id,
              'previous_mfa_mode', @previous_mfa_mode,
              'mfa_mode', @mfa_mode,
              'revoked_challenge_count', @revoked_challenge_count,
              'reason', @reason
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("request_id", requestId);
                command.Parameters.AddWithValue("previous_mfa_mode", reset.PreviousMfaMode);
                command.Parameters.AddWithValue("mfa_mode", reset.MfaMode);
                command.Parameters.AddWithValue("revoked_challenge_count", reset.RevokedChallengeCount);
                command.Parameters.AddWithValue("reason", normalizedReason);
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new MfaRecoveryExecutionResult
        {
            RequestId = requestId,
            AccountId = request.AccountId,
            PreviousMfaMode = reset.PreviousMfaMode,
            MfaMode = reset.MfaMode,
            RevokedChallengeCount = reset.RevokedChallengeCount,
            ExecutionReason = normalizedReason,
            ExecutedAtUtc = executedAtUtc
        };
    }

    private static async Task<CompanyRecord?> ReadCompanyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, entity_number, legal_name, status
            from companies
            where id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CompanyRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("entity_number")).Trim(),
            reader.GetString(reader.GetOrdinal("legal_name")).Trim(),
            reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant());
    }

    private static async Task<AccountRecord?> ReadAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, email, coalesce(username, '') as username, status, mfa_mode
            from users
            where id = @account_id
            limit 1;
            """;
        command.Parameters.AddWithValue("account_id", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AccountRecord(
            UserId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.GetString(reader.GetOrdinal("username")).Trim(),
            reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant(),
            NormalizeMfaMode(reader.GetString(reader.GetOrdinal("mfa_mode"))));
    }

    private static async Task<MfaRecoveryRequestRecord?> ReadMfaRecoveryRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              user_id,
              current_mfa_mode,
              status,
              request_reason,
              requested_at,
              coalesce(review_reason, '') as review_reason,
              reviewed_at
            from account_mfa_recovery_requests
            where id = @id
            limit 1
            for update;
            """;
        command.Parameters.AddWithValue("id", requestId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MfaRecoveryRequestRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            UserId.Parse(reader.GetString(reader.GetOrdinal("user_id"))),
            NormalizeMfaMode(reader.GetString(reader.GetOrdinal("current_mfa_mode"))),
            reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant(),
            reader.GetString(reader.GetOrdinal("request_reason")).Trim(),
            CoerceTimestamp(reader.GetValue(reader.GetOrdinal("requested_at"))),
            reader.GetString(reader.GetOrdinal("review_reason")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("reviewed_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("reviewed_at"))));
    }

    private static async Task<Guid?> ReadOpenMfaRecoveryRequestForAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id
            from account_mfa_recovery_requests
            where user_id = @user_id
              and status in ('requested', 'approved')
            order by requested_at desc
            limit 1;
            """;
        command.Parameters.AddWithValue("user_id", accountId);

        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            Guid requestId => requestId,
            _ => null
        };
    }

    private static ManagedPlatformAccountSummary ReadManagedUserSummary(NpgsqlDataReader reader)
    {
        var companyCodes = reader.GetString(reader.GetOrdinal("company_codes"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var status = reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant();

        return new ManagedPlatformAccountSummary
        {
            AccountId = UserId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            DisplayName = reader.GetString(reader.GetOrdinal("display_name")).Trim(),
            Email = reader.GetString(reader.GetOrdinal("email")).Trim(),
            Username = reader.GetString(reader.GetOrdinal("username")).Trim(),
            Status = status,
            MfaMode = NormalizeMfaMode(reader.GetString(reader.GetOrdinal("mfa_mode"))),
            ActiveMfaRecoveryStatus = reader.GetString(reader.GetOrdinal("active_mfa_recovery_status")).Trim(),
            LastMfaResetAtUtc = reader.IsDBNull(reader.GetOrdinal("last_mfa_reset_at_utc"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("last_mfa_reset_at_utc"))),
            LastMfaResetReason = reader.GetString(reader.GetOrdinal("last_mfa_reset_reason")).Trim(),
            CompanyCodes = companyCodes
        };
    }

    private static async Task<AccountMfaResetGovernanceResult> ResetAccountMfaCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AccountRecord current,
        string normalizedReason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        var revokedChallengeCount = await RevokeActiveMfaChallengesAsync(
            connection,
            transaction,
            current.Id,
            cancellationToken);
        var revokedTotpEnrollmentCount = await RevokeActiveTotpEnrollmentsAsync(
            connection,
            transaction,
            current.Id,
            cancellationToken);

        var previousMfaMode = NormalizeMfaMode(current.MfaMode);
        if (!string.Equals(previousMfaMode, "none", StringComparison.Ordinal) ||
            revokedChallengeCount > 0 ||
            revokedTotpEnrollmentCount > 0)
        {
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    update users
                    set mfa_mode = 'none',
                        security_stamp = gen_random_uuid()::text,
                        updated_at = now()
                    where id = @account_id;
                    """;
                update.Parameters.AddWithValue("account_id", current.Id.Value);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(
                connection,
                transaction,
                companyId: null,
                sysAdminAccountId,
                "platform_account",
                current.Id.Value,
                "account_mfa_reset",
                """
                jsonb_build_object(
                  'previous_mfa_mode', @previous_mfa_mode,
                  'mfa_mode', 'none',
                  'revoked_challenge_count', @revoked_challenge_count,
                  'revoked_totp_enrollment_count', @revoked_totp_enrollment_count,
                  'reason', @reason
                )
                """,
                command =>
                {
                    command.Parameters.AddWithValue("previous_mfa_mode", previousMfaMode);
                    command.Parameters.AddWithValue("revoked_challenge_count", revokedChallengeCount);
                    command.Parameters.AddWithValue("revoked_totp_enrollment_count", revokedTotpEnrollmentCount);
                    command.Parameters.AddWithValue("reason", normalizedReason);
                },
                cancellationToken);
        }

        return new AccountMfaResetGovernanceResult
        {
            AccountId = current.Id,
            Email = current.Email,
            Username = current.Username,
            PreviousMfaMode = previousMfaMode,
            MfaMode = "none",
            RevokedChallengeCount = revokedChallengeCount,
            Reason = normalizedReason,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static async Task<int> RevokeActiveMfaChallengesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            with deleted as (
              delete from business_session_mfa_challenges
              where user_id = @account_id
                and consumed_at is null
              returning 1
            )
            select count(*)
            from deleted;
            """;
        command.Parameters.AddWithValue("account_id", accountId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar switch
        {
            int count => count,
            long longCount => (int)longCount,
            _ => 0
        };
    }

    private static async Task<int> RevokeActiveTotpEnrollmentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            with updated as (
              update account_mfa_totp_enrollments
              set status = 'revoked',
                  revoked_at = now()
              where user_id = @account_id
                and status in ('pending', 'active')
              returning 1
            )
            select count(*)
            from updated;
            """;
        command.Parameters.AddWithValue("account_id", accountId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar switch
        {
            int count => count,
            long longCount => (int)longCount,
            _ => 0
        };
    }

    private static async Task<Guid> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId? companyId,
        UserId? sysAdminAccountId,
        string entityType,
        string entityId,
        string action,
        string payloadExpression,
        Action<NpgsqlCommand> bindPayload,
        CancellationToken cancellationToken)
    {
        var auditId = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            insert into audit_logs (
              id,
              company_id,
              actor_type,
              actor_id,
              entity_type,
              entity_id,
              action,
              payload,
              created_at
            )
            values (
              @id,
              @company_id,
              'sysadmin',
              @actor_id,
              @entity_type,
              @entity_id,
              @action,
              {payloadExpression},
              now()
            );
            """;
        command.Parameters.AddWithValue("id", auditId);
        command.Parameters.AddWithValue("company_id", companyId.HasValue ? companyId.Value.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("actor_id", sysAdminAccountId.HasValue ? sysAdminAccountId.Value.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.AddWithValue("entity_id", entityId);
        command.Parameters.AddWithValue("action", action);
        bindPayload(command);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return auditId;
    }

    private static PlatformAuditEvent ReadAuditEvent(NpgsqlDataReader reader)
    {
        using var payloadDocument = JsonDocument.Parse(reader.GetString(reader.GetOrdinal("payload")));
        var payload = payloadDocument.RootElement;
        var action = reader.GetString(reader.GetOrdinal("action")).Trim().ToLowerInvariant();
        var entityType = reader.GetString(reader.GetOrdinal("entity_type")).Trim().ToLowerInvariant();
        var companyCode = reader.GetString(reader.GetOrdinal("company_entity_number")).Trim();
        var companyName = reader.GetString(reader.GetOrdinal("company_legal_name")).Trim();
        var highlights = BuildHighlights(action, payload);

        return new PlatformAuditEvent
        {
            AuditId = reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId = reader.IsDBNull(reader.GetOrdinal("company_id"))
                ? null
                : CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            CompanyCode = companyCode,
            CompanyName = companyName,
            ScopeLabel = PlatformAuditEvent.BuildScopeLabel(companyName, companyCode),
            ActorType = reader.GetString(reader.GetOrdinal("actor_type")).Trim().ToLowerInvariant(),
            ActorId = reader.IsDBNull(reader.GetOrdinal("actor_id"))
                ? null
                : UserId.Parse(reader.GetString(reader.GetOrdinal("actor_id"))),
            ActorDisplayName = ResolveActorDisplayName(reader),
            ActorEmail = ResolveActorEmail(reader),
            EntityType = entityType,
            EntityId = reader.GetGuid(reader.GetOrdinal("entity_id")),
            EntityLabel = ResolveEntityLabel(entityType, reader),
            Action = action,
            ActionLabel = PlatformAuditEvent.GetActionLabel(action),
            Detail = BuildDetail(action, payload),
            Reason = ReadString(payload, "reason", "Reason"),
            Highlights = highlights,
            CreatedAtUtc = CoerceTimestamp(reader.GetValue(reader.GetOrdinal("created_at")))
        };
    }

    private static string ResolveActorDisplayName(NpgsqlDataReader reader)
    {
        var actorType = reader.GetString(reader.GetOrdinal("actor_type")).Trim().ToLowerInvariant();
        var sysAdminDisplayName = reader.GetString(reader.GetOrdinal("sysadmin_display_name")).Trim();
        if (!string.IsNullOrWhiteSpace(sysAdminDisplayName))
        {
            return sysAdminDisplayName;
        }

        var actorUsername = reader.GetString(reader.GetOrdinal("actor_username")).Trim();
        if (!string.IsNullOrWhiteSpace(actorUsername))
        {
            return actorUsername;
        }

        if (string.Equals(actorType, "system", StringComparison.Ordinal))
        {
            return "System";
        }

        return ResolveActorEmail(reader);
    }

    private static string ResolveActorEmail(NpgsqlDataReader reader)
    {
        var sysAdminEmail = reader.GetString(reader.GetOrdinal("sysadmin_email")).Trim();
        if (!string.IsNullOrWhiteSpace(sysAdminEmail))
        {
            return sysAdminEmail;
        }

        return reader.GetString(reader.GetOrdinal("actor_email")).Trim();
    }

    private static string ResolveEntityLabel(string entityType, NpgsqlDataReader reader) =>
        entityType switch
        {
            "company" => PlatformAuditEvent.BuildScopeLabel(
                reader.GetString(reader.GetOrdinal("company_legal_name")).Trim(),
                reader.GetString(reader.GetOrdinal("company_entity_number")).Trim()),
            "platform_account" => BuildUserLabel(
                reader.GetString(reader.GetOrdinal("entity_username")).Trim(),
                reader.GetString(reader.GetOrdinal("entity_email")).Trim(),
                fallback: "Platform Account"),
            "company_membership" => BuildUserLabel(
                reader.GetString(reader.GetOrdinal("membership_username")).Trim(),
                reader.GetString(reader.GetOrdinal("membership_email")).Trim(),
                fallback: "Company Membership"),
            "sysadmin_account" => BuildUserLabel(
                reader.GetString(reader.GetOrdinal("entity_sysadmin_display_name")).Trim(),
                reader.GetString(reader.GetOrdinal("entity_sysadmin_email")).Trim(),
                fallback: "SysAdmin Account"),
            _ => entityType
        };

    private static string BuildUserLabel(string username, string email, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(email))
        {
            return $"{username} ({email})";
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            return username;
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }

        return fallback;
    }

    private static string BuildDetail(string action, JsonElement payload) =>
        action switch
        {
            "company_status_changed" => BuildTransitionDetail(
                ReadString(payload, "previous_status"),
                ReadString(payload, "status")),
            "account_status_changed" => BuildAccountStatusDetail(payload),
            "account_totp_enrollment_started" => BuildTotpEnrollmentDetail(payload),
            "account_totp_enrollment_confirmed" => BuildTotpEnrollmentDetail(payload),
            "account_mfa_recovery_requested" => BuildMfaRecoveryDetail(payload),
            "account_mfa_recovery_approved" => BuildMfaRecoveryDetail(payload),
            "account_mfa_recovery_rejected" => BuildMfaRecoveryDetail(payload),
            "account_mfa_recovery_executed" => BuildAccountMfaResetDetail(payload),
            "account_mfa_reset" => BuildAccountMfaResetDetail(payload),
            "email_change_requested" => BuildPasswordResetDetail(payload),
            "email_change_dispatched" => BuildPasswordResetDeliveryOutcomeDetail(payload),
            "email_change_dispatch_failed" => BuildPasswordResetDeliveryOutcomeDetail(payload),
            "password_change_requested" => BuildPasswordResetDetail(payload),
            "password_change_dispatched" => BuildPasswordResetDeliveryOutcomeDetail(payload),
            "password_change_dispatch_failed" => BuildPasswordResetDeliveryOutcomeDetail(payload),
            "password_reset_requested" => BuildPasswordResetDetail(payload),
            "password_reset_dispatched" => BuildPasswordResetDeliveryOutcomeDetail(payload),
            "password_reset_dispatch_failed" => BuildPasswordResetDeliveryOutcomeDetail(payload),
            "membership_role_changed" => BuildTransitionDetail(
                ReadString(payload, "previous_role", "PreviousRole"),
                ReadString(payload, "role", "Role")),
            "membership_permissions_saved" => PlatformAuditEvent.BuildPermissionChangeDetail(
                ReadTokens(payload, "added_permission_tokens", "AddedPermissionTokens"),
                ReadTokens(payload, "removed_permission_tokens", "RemovedPermissionTokens")),
            "sysadmin_first_account_created" => ReadString(payload, "provisioning_mode"),
            "sysadmin_password_rotated" => ReadString(payload, "rotation_mode"),
            _ => string.Empty
        };

    private static string BuildTransitionDetail(string previousValue, string currentValue)
    {
        if (string.IsNullOrWhiteSpace(previousValue) && string.IsNullOrWhiteSpace(currentValue))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(previousValue))
        {
            return currentValue;
        }

        if (string.IsNullOrWhiteSpace(currentValue))
        {
            return previousValue;
        }

        return $"{previousValue} -> {currentValue}";
    }

    private static string BuildAccountStatusDetail(JsonElement payload)
    {
        var detail = BuildTransitionDetail(
            ReadString(payload, "previous_status"),
            ReadString(payload, "status"));
        var lockedUntil = ReadString(payload, "locked_until_utc");

        if (string.IsNullOrWhiteSpace(lockedUntil))
        {
            return detail;
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"Locked until {lockedUntil}"
            : $"{detail} | locked until {lockedUntil}";
    }

    private static string BuildAccountMfaResetDetail(JsonElement payload)
    {
        var detail = BuildTransitionDetail(
            ReadString(payload, "previous_mfa_mode"),
            ReadString(payload, "mfa_mode"));
        var revokedChallengeCount = ReadString(payload, "revoked_challenge_count");
        var revokedTotpEnrollmentCount = ReadString(payload, "revoked_totp_enrollment_count");

        if (string.IsNullOrWhiteSpace(revokedChallengeCount) &&
            string.IsNullOrWhiteSpace(revokedTotpEnrollmentCount))
        {
            return detail;
        }

        var segments = new List<string>();
        AddIfPresent(segments, detail);
        if (!string.IsNullOrWhiteSpace(revokedChallengeCount))
        {
            segments.Add($"revoked challenges: {revokedChallengeCount}");
        }

        if (!string.IsNullOrWhiteSpace(revokedTotpEnrollmentCount))
        {
            segments.Add($"revoked totp enrollments: {revokedTotpEnrollmentCount}");
        }

        return string.Join(" | ", segments);
    }

    private static string BuildMfaRecoveryDetail(JsonElement payload)
    {
        var currentMfaMode = ReadString(payload, "current_mfa_mode");
        var status = ReadString(payload, "status");

        var segments = new List<string>();
        AddIfPresent(segments, currentMfaMode);
        AddIfPresent(segments, status);
        return string.Join(" | ", segments);
    }

    private static string BuildTotpEnrollmentDetail(JsonElement payload)
    {
        var segments = new List<string>();
        AddIfPresent(segments, ReadString(payload, "mfa_mode"));
        AddIfPresent(segments, ReadString(payload, "status"));
        AddIfPresent(segments, ReadString(payload, "expires_at_utc"));
        return string.Join(" | ", segments);
    }

    private static string BuildPasswordResetDetail(JsonElement payload)
    {
        var deliveryStatus = ReadString(payload, "delivery_status");
        var maskedDestination = ReadString(payload, "masked_destination");
        var expiresAt = ReadString(payload, "expires_at_utc");

        var segments = new List<string>();
        if (!string.IsNullOrWhiteSpace(deliveryStatus))
        {
            segments.Add(deliveryStatus);
        }

        if (!string.IsNullOrWhiteSpace(maskedDestination))
        {
            segments.Add(maskedDestination);
        }

        if (!string.IsNullOrWhiteSpace(expiresAt))
        {
            segments.Add($"expires {expiresAt}");
        }

        return string.Join(" | ", segments);
    }

    private static string BuildPasswordResetDeliveryOutcomeDetail(JsonElement payload)
    {
        var providerKey = ReadString(payload, "provider_key");
        var maskedDestination = ReadString(payload, "masked_destination");
        var failure = ReadString(payload, "failure_message");

        var segments = new List<string>();
        AddIfPresent(segments, providerKey);
        AddIfPresent(segments, maskedDestination);
        AddIfPresent(segments, failure);
        return string.Join(" | ", segments);
    }

    private static IReadOnlyList<string> BuildHighlights(string action, JsonElement payload)
    {
        var highlights = new List<string>();

        switch (action)
        {
            case "company_status_changed":
            case "account_status_changed":
                AddIfPresent(highlights, ReadString(payload, "previous_status"));
                AddIfPresent(highlights, ReadString(payload, "status"));
                AddIfPresent(highlights, ReadString(payload, "locked_until_utc"));
                break;

            case "account_mfa_reset":
                AddIfPresent(highlights, ReadString(payload, "previous_mfa_mode"));
                AddIfPresent(highlights, ReadString(payload, "mfa_mode"));
                AddIfPresent(highlights, ReadString(payload, "revoked_challenge_count"));
                AddIfPresent(highlights, ReadString(payload, "revoked_totp_enrollment_count"));
                break;

            case "account_totp_enrollment_started":
            case "account_totp_enrollment_confirmed":
                AddIfPresent(highlights, ReadString(payload, "mfa_mode"));
                AddIfPresent(highlights, ReadString(payload, "status"));
                break;

            case "account_mfa_recovery_requested":
            case "account_mfa_recovery_approved":
            case "account_mfa_recovery_rejected":
                AddIfPresent(highlights, ReadString(payload, "current_mfa_mode"));
                AddIfPresent(highlights, ReadString(payload, "status"));
                break;

            case "account_mfa_recovery_executed":
                AddIfPresent(highlights, ReadString(payload, "previous_mfa_mode"));
                AddIfPresent(highlights, ReadString(payload, "mfa_mode"));
                AddIfPresent(highlights, ReadString(payload, "revoked_challenge_count"));
                AddIfPresent(highlights, ReadString(payload, "revoked_totp_enrollment_count"));
                break;

            case "password_reset_requested":
            case "email_change_requested":
            case "password_change_requested":
                AddIfPresent(highlights, ReadString(payload, "delivery_status"));
                AddIfPresent(highlights, ReadString(payload, "masked_destination"));
                break;

            case "password_reset_dispatched":
            case "password_reset_dispatch_failed":
            case "email_change_dispatched":
            case "email_change_dispatch_failed":
            case "password_change_dispatched":
            case "password_change_dispatch_failed":
                AddIfPresent(highlights, ReadString(payload, "provider_key"));
                AddIfPresent(highlights, ReadString(payload, "masked_destination"));
                AddIfPresent(highlights, ReadString(payload, "failure_message"));
                break;

            case "membership_role_changed":
                AddIfPresent(highlights, ReadString(payload, "previous_role", "PreviousRole"));
                AddIfPresent(highlights, ReadString(payload, "role", "Role"));
                break;

            case "membership_permissions_saved":
                highlights.AddRange(
                    ReadTokens(payload, "added_permission_tokens", "AddedPermissionTokens")
                        .Select(static token => $"+ {token}"));
                highlights.AddRange(
                    ReadTokens(payload, "removed_permission_tokens", "RemovedPermissionTokens")
                        .Select(static token => $"- {token}"));
                break;

            case "sysadmin_first_account_created":
            case "sysadmin_password_rotated":
                AddIfPresent(highlights, ReadString(payload, "email"));
                AddIfPresent(highlights, ReadString(payload, "provisioning_mode"));
                AddIfPresent(highlights, ReadString(payload, "rotation_mode"));
                break;
        }

        return highlights;
    }

    private static void AddIfPresent(List<string> highlights, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            highlights.Add(value);
        }
    }

    private static async Task FinalizeDispatchAsSentAsync(
        NpgsqlConnection connection,
        Guid dispatchId,
        Guid requestAuditId,
        UserId accountId,
        UserId? sysAdminAccountId,
        string destination,
        PlatformNotificationSendResult sendResult,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using (var updateDispatch = connection.CreateCommand())
        {
            updateDispatch.CommandText =
                """
                update platform_notification_dispatches
                set status = 'sent',
                    provider_key = @provider_key,
                    attempt_count = attempt_count + 1,
                    sent_at = @sent_at,
                    failed_at = null,
                    last_error = null,
                    updated_at = now(),
                    payload = payload || jsonb_build_object(
                      'provider_key', @provider_key,
                      'delivery_mode', 'smtp_provider',
                      'delivery_status', 'sent',
                      'sent_at_utc', @sent_at,
                      'external_reference', @external_reference
                    )
                where id = @id;
                """;
            updateDispatch.Parameters.AddWithValue("id", dispatchId);
            updateDispatch.Parameters.AddWithValue("provider_key", sendResult.ProviderKey);
            updateDispatch.Parameters.AddWithValue("sent_at", now);
            updateDispatch.Parameters.AddWithValue(
                "external_reference",
                string.IsNullOrWhiteSpace(sendResult.ExternalReference)
                    ? DBNull.Value
                    : sendResult.ExternalReference);
            await updateDispatch.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendPasswordResetDeliveryAuditAsync(
            connection,
            requestAuditId,
            accountId,
            sysAdminAccountId,
            "password_reset_dispatched",
            destination,
            sendResult,
            failureMessage: null,
            cancellationToken);
    }

    private static async Task FinalizeDispatchAsFailedAsync(
        NpgsqlConnection connection,
        Guid dispatchId,
        Guid requestAuditId,
        UserId accountId,
        UserId? sysAdminAccountId,
        string destination,
        PlatformNotificationSendResult sendResult,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using (var updateDispatch = connection.CreateCommand())
        {
            updateDispatch.CommandText =
                """
                update platform_notification_dispatches
                set status = 'failed',
                    provider_key = @provider_key,
                    attempt_count = attempt_count + 1,
                    failed_at = @failed_at,
                    last_error = @last_error,
                    updated_at = now(),
                    payload = payload || jsonb_build_object(
                      'provider_key', @provider_key,
                      'delivery_mode', 'smtp_provider',
                      'delivery_status', 'failed',
                      'failed_at_utc', @failed_at,
                      'failure_message', @last_error
                    )
                where id = @id;
                """;
            updateDispatch.Parameters.AddWithValue("id", dispatchId);
            updateDispatch.Parameters.AddWithValue("provider_key", sendResult.ProviderKey);
            updateDispatch.Parameters.AddWithValue("failed_at", now);
            updateDispatch.Parameters.AddWithValue("last_error", sendResult.FailureMessage);
            await updateDispatch.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendPasswordResetDeliveryAuditAsync(
            connection,
            requestAuditId,
            accountId,
            sysAdminAccountId,
            "password_reset_dispatch_failed",
            destination,
            sendResult,
            sendResult.FailureMessage,
            cancellationToken);
    }

    private static async Task AppendPasswordResetDeliveryAuditAsync(
        NpgsqlConnection connection,
        Guid requestAuditId,
        UserId accountId,
        UserId? sysAdminAccountId,
        string action,
        string destination,
        PlatformNotificationSendResult sendResult,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
              payload,
              created_at
            )
            values (
              @id,
              null,
              'sysadmin',
              @actor_id,
              'platform_account',
              @entity_id,
              @action,
              jsonb_build_object(
                'request_audit_id', @request_audit_id,
                'provider_key', @provider_key,
                'masked_destination', @masked_destination,
                'failure_message', @failure_message
              ),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("actor_id", sysAdminAccountId.HasValue ? sysAdminAccountId.Value : DBNull.Value);
        command.Parameters.AddWithValue("entity_id", accountId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("request_audit_id", requestAuditId);
        command.Parameters.AddWithValue("provider_key", sendResult.ProviderKey);
        command.Parameters.AddWithValue("masked_destination", MaskEmail(destination));
        command.Parameters.AddWithValue(
            "failure_message",
            string.IsNullOrWhiteSpace(failureMessage) ? DBNull.Value : failureMessage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadString(JsonElement payload, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (payload.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ReadTokens(JsonElement payload, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!payload.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return value
                .EnumerateArray()
                .Where(static element => element.ValueKind == JsonValueKind.String)
                .Select(static element => element.GetString())
                .Where(static token => !string.IsNullOrWhiteSpace(token))
                .Select(static token => token!.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static token => token, StringComparer.Ordinal)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static string NormalizeRequired(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeMfaMode(string? mfaMode) =>
        string.IsNullOrWhiteSpace(mfaMode)
            ? "none"
            : mfaMode.Trim().ToLowerInvariant();

    private static string NormalizeRecoveryDecision(string decision)
    {
        var normalized = NormalizeRequired(decision, "MFA recovery decision");
        return normalized switch
        {
            "approve" or "approved" => "approve",
            "reject" or "rejected" => "reject",
            _ => throw new InvalidOperationException("MFA recovery decision must be approve or reject.")
        };
    }

    private static string NormalizeReason(string reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();

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

    private static string MaskEmail(string email)
    {
        var normalized = email.Trim();
        var atIndex = normalized.IndexOf('@');
        if (atIndex <= 1)
        {
            return "***";
        }

        return $"{normalized[..1]}***{normalized[(atIndex - 1)..]}";
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

    private static void EnsureAllowed(string value, IReadOnlyCollection<string> allowed, string fieldName)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported {fieldName} '{value}'.");
        }
    }

    private sealed record CompanyRecord(
        Guid Id,
        string EntityNumber,
        string LegalName,
        string Status);

    private sealed record AccountRecord(
        UserId Id,
        string Email,
        string Username,
        string Status,
        string MfaMode);

    private sealed record MfaRecoveryRequestRecord(
        Guid Id,
        UserId AccountId,
        string CurrentMfaMode,
        string Status,
        string RequestReason,
        DateTimeOffset RequestedAtUtc,
        string ReviewReason,
        DateTimeOffset? ReviewedAtUtc);
}
