using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Platform.Core.Runtime;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed partial class PostgresPlatformAccountProfileRepository(
    PlatformPostgresConnectionFactory connectionFactory,
    IPlatformRuntimeStateRepository runtimeStateRepository,
    IPlatformVerificationNotificationSender notificationSender) : IPlatformAccountProfileRepository
{
    private const string EmailChangePurpose = "email_change";
    private const string PasswordChangePurpose = "password_change";
    private const string NoMfaMode = "none";
    private const string EmailCodeMfaMode = "email_code";

    public async Task<PlatformAccountProfileSummary?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await BuildSummaryAsync(connection, null, userId, cancellationToken);
    }

    public async Task<PlatformAccountProfileSummary?> SaveDisplayNameAsync(
        Guid userId,
        string displayName,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInteractiveWritesAllowedAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadAccountAsync(connection, transaction, userId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update users
                set display_name = @display_name,
                    updated_at = now()
                where id = @user_id;
                """;
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("display_name", displayName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            "profile_display_name_saved",
            """
            jsonb_build_object(
              'previous_display_name', @previous_display_name,
              'display_name', @display_name
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("previous_display_name", current.DisplayName);
                command.Parameters.AddWithValue("display_name", displayName);
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(userId, cancellationToken);
    }

    public async Task<PlatformAccountProfileSummary?> SaveMfaModeAsync(
        Guid userId,
        string mfaMode,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInteractiveWritesAllowedAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadAccountAsync(connection, transaction, userId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (string.Equals(mfaMode, EmailCodeMfaMode, StringComparison.Ordinal))
        {
            if (!current.EmailVerifiedAtUtc.HasValue)
            {
                throw new InvalidOperationException("Email verification is required before enabling email-code MFA.");
            }

            await EnsureVerificationDeliveryReadyAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update users
                set mfa_mode = @mfa_mode,
                    updated_at = now()
                where id = @user_id;
                """;
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("mfa_mode", mfaMode);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            "profile_mfa_mode_saved",
            """
            jsonb_build_object(
              'previous_mfa_mode', @previous_mfa_mode,
              'mfa_mode', @mfa_mode
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("previous_mfa_mode", current.MfaMode);
                command.Parameters.AddWithValue("mfa_mode", mfaMode);
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(userId, cancellationToken);
    }

    public Task<PlatformProfileChangeRequestResult?> RequestEmailChangeAsync(
        Guid userId,
        string newEmail,
        CancellationToken cancellationToken) =>
        RequestVerificationAsync(
            userId,
            EmailChangePurpose,
            newEmail,
            "jsonb_build_object('new_email', @payload_value)",
            "email_change_verification",
            cancellationToken);

    public Task<PlatformProfileChangeRequestResult?> RequestPasswordChangeAsync(
        Guid userId,
        string newPasswordHash,
        CancellationToken cancellationToken) =>
        RequestVerificationAsync(
            userId,
            PasswordChangePurpose,
            destinationOverride: null,
            "jsonb_build_object('new_password_hash', @payload_value)",
            "password_change_verification",
            cancellationToken,
            newPasswordHash);

    public async Task<PlatformProfileChangeConfirmationResult?> ConfirmEmailChangeAsync(
        Guid userId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInteractiveWritesAllowedAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var account = await ReadAccountAsync(connection, transaction, userId, cancellationToken);
        if (account is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var verification = await ReadActiveVerificationAsync(connection, transaction, userId, EmailChangePurpose, cancellationToken);
        EnsureVerificationExists(verification, "email change");
        var emailVerification = verification!;
        await EnsureVerificationCodeMatchesAsync(connection, transaction, emailVerification, verificationCode, cancellationToken);

        var newEmail = ReadPayloadValue(emailVerification.PayloadJson, "new_email");
        if (string.IsNullOrWhiteSpace(newEmail))
        {
            throw new InvalidOperationException("The pending email change does not contain a target email.");
        }

        await EnsureEmailAvailableAsync(connection, transaction, userId, newEmail, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update users
                set email = @email,
                    email_verified_at = now(),
                    security_stamp = gen_random_uuid()::text,
                    updated_at = now()
                where id = @user_id;
                """;
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("email", newEmail);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ConsumeVerificationAsync(connection, transaction, emailVerification.Id, cancellationToken);
        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            "email_change_confirmed",
            """
            jsonb_build_object(
              'masked_previous_destination', @masked_previous_destination,
              'masked_destination', @masked_destination
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("masked_previous_destination", MaskEmail(account.Email));
                command.Parameters.AddWithValue("masked_destination", MaskEmail(newEmail));
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var profile = await GetAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("Platform account was not found after confirming the email change.");

        return new PlatformProfileChangeConfirmationResult
        {
            ChangeType = EmailChangePurpose,
            ConfirmedAtUtc = DateTimeOffset.UtcNow,
            Profile = profile
        };
    }

    public async Task<PlatformProfileChangeConfirmationResult?> ConfirmPasswordChangeAsync(
        Guid userId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInteractiveWritesAllowedAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var account = await ReadAccountAsync(connection, transaction, userId, cancellationToken);
        if (account is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var verification = await ReadActiveVerificationAsync(connection, transaction, userId, PasswordChangePurpose, cancellationToken);
        EnsureVerificationExists(verification, "password change");
        var passwordVerification = verification!;
        await EnsureVerificationCodeMatchesAsync(connection, transaction, passwordVerification, verificationCode, cancellationToken);

        var newPasswordHash = ReadPayloadValue(passwordVerification.PayloadJson, "new_password_hash");
        if (string.IsNullOrWhiteSpace(newPasswordHash))
        {
            throw new InvalidOperationException("The pending password change does not contain a password hash.");
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update users
                set password_hash = @password_hash,
                    security_stamp = gen_random_uuid()::text,
                    updated_at = now()
                where id = @user_id;
                """;
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("password_hash", newPasswordHash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ConsumeVerificationAsync(connection, transaction, passwordVerification.Id, cancellationToken);
        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            "password_change_confirmed",
            """
            jsonb_build_object(
              'masked_destination', @masked_destination
            )
            """,
            command => command.Parameters.AddWithValue("masked_destination", MaskEmail(account.Email)),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var profile = await GetAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("Platform account was not found after confirming the password change.");

        return new PlatformProfileChangeConfirmationResult
        {
            ChangeType = PasswordChangePurpose,
            ConfirmedAtUtc = DateTimeOffset.UtcNow,
            Profile = profile
        };
    }

    private async Task<PlatformProfileChangeRequestResult?> RequestVerificationAsync(
        Guid userId,
        string purpose,
        string? destinationOverride,
        string payloadSql,
        string notificationType,
        CancellationToken cancellationToken,
        string? payloadValue = null)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInteractiveWritesAllowedAsync(cancellationToken);
        await EnsureVerificationDeliveryReadyAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var account = await ReadAccountAsync(connection, transaction, userId, cancellationToken);
        if (account is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var destination = ResolveDestination(purpose, account.Email, destinationOverride);
        if (string.Equals(purpose, EmailChangePurpose, StringComparison.Ordinal) &&
            string.Equals(account.Email, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The new email must be different from the current platform email.");
        }

        if (string.Equals(purpose, EmailChangePurpose, StringComparison.Ordinal))
        {
            await EnsureEmailAvailableAsync(connection, transaction, userId, destination, cancellationToken);
        }

        await InvalidateActiveVerificationsAsync(connection, transaction, userId, purpose, cancellationToken);

        var verificationCodeId = Guid.NewGuid();
        var verificationCode = CreateVerificationCode();
        var verificationCodeHash = HashVerificationCode(verificationCode);
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15);
        var dispatchId = Guid.NewGuid();
        var requestAuditId = Guid.NewGuid();
        var maskedDestination = MaskEmail(destination);

        await using (var insertVerification = connection.CreateCommand())
        {
            insertVerification.Transaction = transaction;
            insertVerification.CommandText =
                $"""
                insert into account_verification_codes (
                  id,
                  user_id,
                  purpose,
                  destination,
                  code_hash,
                  expires_at,
                  payload
                )
                values (
                  @id,
                  @user_id,
                  @purpose,
                  @destination,
                  @code_hash,
                  @expires_at,
                  {payloadSql}
                );
                """;
            insertVerification.Parameters.AddWithValue("id", verificationCodeId);
            insertVerification.Parameters.AddWithValue("user_id", userId);
            insertVerification.Parameters.AddWithValue("purpose", purpose);
            insertVerification.Parameters.AddWithValue("destination", destination);
            insertVerification.Parameters.AddWithValue("code_hash", verificationCodeHash);
            insertVerification.Parameters.AddWithValue("expires_at", expiresAtUtc);
            insertVerification.Parameters.AddWithValue("payload_value", payloadValue ?? destination);
            await insertVerification.ExecuteNonQueryAsync(cancellationToken);
        }

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
                  @notification_type,
                  @destination,
                  'queued',
                  jsonb_build_object(
                    'account_id', @account_id,
                    'verification_code_id', @verification_code_id,
                    'purpose', @purpose,
                    'masked_destination', @masked_destination,
                    'delivery_mode', 'smtp_provider_pending'
                  )
                );
                """;
            insertDispatch.Parameters.AddWithValue("id", dispatchId);
            insertDispatch.Parameters.AddWithValue("notification_type", notificationType);
            insertDispatch.Parameters.AddWithValue("destination", destination);
            insertDispatch.Parameters.AddWithValue("account_id", userId);
            insertDispatch.Parameters.AddWithValue("verification_code_id", verificationCodeId);
            insertDispatch.Parameters.AddWithValue("purpose", purpose);
            insertDispatch.Parameters.AddWithValue("masked_destination", maskedDestination);
            await insertDispatch.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            $"{purpose}_requested",
            """
            jsonb_build_object(
              'masked_destination', @masked_destination,
              'expires_at_utc', @expires_at_utc,
              'delivery_status', 'queued'
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("masked_destination", maskedDestination);
                command.Parameters.AddWithValue("expires_at_utc", expiresAtUtc);
            },
            cancellationToken,
            requestAuditId);

        await transaction.CommitAsync(cancellationToken);

        var sendResult = await notificationSender.SendVerificationAsync(
            new PlatformVerificationNotificationMessage
            {
                DispatchId = dispatchId,
                UserId = userId,
                Purpose = purpose,
                Destination = destination,
                RecipientDisplayName = account.DisplayName,
                VerificationCode = verificationCode,
                ExpiresAtUtc = expiresAtUtc
            },
            cancellationToken);

        await using var finalizeConnection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (sendResult.Succeeded)
        {
            await FinalizeDispatchAsSentAsync(
                finalizeConnection,
                dispatchId,
                requestAuditId,
                userId,
                $"{purpose}_dispatched",
                destination,
                sendResult,
                cancellationToken);
        }
        else
        {
            await FinalizeDispatchAsFailedAsync(
                finalizeConnection,
                dispatchId,
                requestAuditId,
                userId,
                $"{purpose}_dispatch_failed",
                destination,
                sendResult,
                cancellationToken);

            throw new PlatformNotificationDeliveryException(
                $"{purpose.Replace('_', ' ')} delivery failed. {sendResult.FailureMessage}".Trim());
        }

        var profile = await GetAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("Platform account was not found after requesting verification.");

        return new PlatformProfileChangeRequestResult
        {
            ChangeType = purpose,
            MaskedDestination = maskedDestination,
            ExpiresAtUtc = expiresAtUtc,
            Profile = profile
        };
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
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

            create index if not exists idx_account_verification_codes_active
              on account_verification_codes (user_id, purpose, expires_at desc)
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

    private async Task EnsureInteractiveWritesAllowedAsync(CancellationToken cancellationToken)
    {
        var maintenanceState = await runtimeStateRepository.GetMaintenanceStateAsync(cancellationToken);
        if (maintenanceState?.Enabled == true)
        {
            throw new InvalidOperationException(
                $"Profile changes are blocked because maintenance mode is enabled. {maintenanceState.Message}".Trim());
        }
    }

    private async Task EnsureVerificationDeliveryReadyAsync(CancellationToken cancellationToken)
    {
        var readiness = await runtimeStateRepository.GetNotificationReadinessStateAsync(cancellationToken);
        if (readiness is null || !readiness.IsVerificationDeliveryReady)
        {
            var blockingReason = readiness?.GetBlockingReason() ?? "Notification readiness has not been configured.";
            throw new InvalidOperationException(
                $"Verification delivery is blocked because notification readiness is not verified. {blockingReason}");
        }

        var configurationError = notificationSender.GetConfigurationError();
        if (!string.IsNullOrWhiteSpace(configurationError))
        {
            throw new InvalidOperationException(
                $"Verification delivery is blocked because the notification provider is not configured. {configurationError}");
        }
    }

    private async Task<PlatformAccountProfileSummary?> BuildSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var record = await ReadProfileRecordAsync(connection, transaction, userId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var readiness = await runtimeStateRepository.GetNotificationReadinessStateAsync(cancellationToken);
        var configurationError = notificationSender.GetConfigurationError();
        var notificationReady = readiness?.IsVerificationDeliveryReady == true && string.IsNullOrWhiteSpace(configurationError);
        var blockingReason = notificationReady
            ? string.Empty
            : !string.IsNullOrWhiteSpace(configurationError) && readiness?.IsVerificationDeliveryReady == true
                ? configurationError
                : readiness?.GetBlockingReason() ?? configurationError ?? "Notification readiness has not been configured.";

        return new PlatformAccountProfileSummary
        {
            UserId = record.Id,
            Username = record.Username,
            DisplayName = record.DisplayName,
            Email = record.Email,
            Status = record.Status,
            EmailVerifiedAtUtc = record.EmailVerifiedAtUtc,
            MfaMode = NormalizeMfaMode(record.MfaMode),
            LastMfaModeChangedAtUtc = record.LastMfaModeChangedAtUtc,
            PreviousMfaMode = NormalizeMfaMode(record.PreviousMfaMode),
            NotificationVerificationReady = notificationReady,
            NotificationBlockingReason = blockingReason,
            PendingEmailChangeMaskedDestination = string.IsNullOrWhiteSpace(record.PendingEmailChangeDestination)
                ? string.Empty
                : MaskEmail(record.PendingEmailChangeDestination),
            PendingEmailChangeExpiresAtUtc = record.PendingEmailChangeExpiresAtUtc,
            PendingPasswordChangeMaskedDestination = string.IsNullOrWhiteSpace(record.PendingPasswordChangeDestination)
                ? string.Empty
                : MaskEmail(record.PendingPasswordChangeDestination),
            PendingPasswordChangeExpiresAtUtc = record.PendingPasswordChangeExpiresAtUtc
        };
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorUserId,
        string action,
        string payloadSql,
        Action<NpgsqlCommand> configureParameters,
        CancellationToken cancellationToken,
        Guid? auditId = null)
    {
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
              null,
              'user',
              @actor_id,
              'platform_account',
              @entity_id,
              @action,
              {payloadSql},
              now()
            );
            """;
        command.Parameters.AddWithValue("id", auditId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("actor_id", actorUserId);
        command.Parameters.AddWithValue("entity_id", actorUserId);
        command.Parameters.AddWithValue("action", action);
        configureParameters(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task FinalizeDispatchAsSentAsync(
        NpgsqlConnection connection,
        Guid dispatchId,
        Guid requestAuditId,
        Guid actorUserId,
        string action,
        string destination,
        PlatformNotificationSendResult sendResult,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
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
            command.Parameters.AddWithValue("id", dispatchId);
            command.Parameters.AddWithValue("provider_key", sendResult.ProviderKey);
            command.Parameters.AddWithValue("sent_at", now);
            command.Parameters.AddWithValue(
                "external_reference",
                string.IsNullOrWhiteSpace(sendResult.ExternalReference)
                    ? DBNull.Value
                    : sendResult.ExternalReference);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendVerificationDeliveryAuditAsync(
            connection,
            requestAuditId,
            actorUserId,
            action,
            destination,
            sendResult,
            failureMessage: null,
            cancellationToken);
    }

    private static async Task FinalizeDispatchAsFailedAsync(
        NpgsqlConnection connection,
        Guid dispatchId,
        Guid requestAuditId,
        Guid actorUserId,
        string action,
        string destination,
        PlatformNotificationSendResult sendResult,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
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
            command.Parameters.AddWithValue("id", dispatchId);
            command.Parameters.AddWithValue("provider_key", sendResult.ProviderKey);
            command.Parameters.AddWithValue("failed_at", now);
            command.Parameters.AddWithValue("last_error", sendResult.FailureMessage);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendVerificationDeliveryAuditAsync(
            connection,
            requestAuditId,
            actorUserId,
            action,
            destination,
            sendResult,
            sendResult.FailureMessage,
            cancellationToken);
    }

    private static async Task AppendVerificationDeliveryAuditAsync(
        NpgsqlConnection connection,
        Guid requestAuditId,
        Guid actorUserId,
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
              'user',
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
        command.Parameters.AddWithValue("actor_id", actorUserId);
        command.Parameters.AddWithValue("entity_id", actorUserId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("request_audit_id", requestAuditId);
        command.Parameters.AddWithValue("provider_key", sendResult.ProviderKey);
        command.Parameters.AddWithValue("masked_destination", MaskEmail(destination));
        command.Parameters.AddWithValue(
            "failure_message",
            string.IsNullOrWhiteSpace(failureMessage) ? DBNull.Value : failureMessage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureEmailAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        string email,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select 1
            from users
            where email = @email
              and id <> @user_id
            limit 1;
            """;
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("user_id", userId);
        var existing = await command.ExecuteScalarAsync(cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("That email address is already in use by another platform account.");
        }
    }

    private static async Task InvalidateActiveVerificationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        string purpose,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update account_verification_codes
            set consumed_at = now()
            where user_id = @user_id
              and purpose = @purpose
              and consumed_at is null;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("purpose", purpose);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ConsumeVerificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid verificationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update account_verification_codes
            set consumed_at = now()
            where id = @id
              and consumed_at is null;
            """;
        command.Parameters.AddWithValue("id", verificationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureVerificationCodeMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VerificationRecord verification,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        if (verification.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("Verification code has expired. Request a new code and try again.");
        }

        if (string.Equals(HashVerificationCode(verificationCode), verification.CodeHash, StringComparison.Ordinal))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update account_verification_codes
            set failed_attempts = failed_attempts + 1
            where id = @id;
            """;
        command.Parameters.AddWithValue("id", verification.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);

        throw new InvalidOperationException("Verification code is invalid.");
    }

    private static void EnsureVerificationExists(VerificationRecord? verification, string label)
    {
        if (verification is null)
        {
            throw new InvalidOperationException($"No pending {label} verification request was found.");
        }
    }

    private static async Task<AccountRecord?> ReadAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              email,
              coalesce(nullif(username, ''), email) as username,
              coalesce(nullif(display_name, ''), nullif(username, ''), email) as display_name,
              status,
              email_verified_at,
              mfa_mode
            from users
            where id = @user_id;
            """;
        command.Parameters.AddWithValue("user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AccountRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.GetString(reader.GetOrdinal("username")).Trim(),
            reader.GetString(reader.GetOrdinal("display_name")).Trim(),
            reader.GetString(reader.GetOrdinal("status")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("email_verified_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("email_verified_at"))),
            NormalizeMfaMode(reader.GetString(reader.GetOrdinal("mfa_mode"))));
    }

    private static async Task<ProfileRecord?> ReadProfileRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              u.id,
              u.email,
              coalesce(nullif(u.username, ''), u.email) as username,
              coalesce(nullif(u.display_name, ''), nullif(u.username, ''), u.email) as display_name,
              u.status,
              u.email_verified_at,
              u.mfa_mode,
              mfa_change.previous_mfa_mode,
              mfa_change.created_at as last_mfa_mode_changed_at,
              email_change.destination as pending_email_change_destination,
              email_change.expires_at as pending_email_change_expires_at,
              password_change.destination as pending_password_change_destination,
              password_change.expires_at as pending_password_change_expires_at
            from users u
            left join lateral (
              select
                payload ->> 'previous_mfa_mode' as previous_mfa_mode,
                created_at
              from audit_logs
              where entity_type = 'platform_account'
                and entity_id = u.id
                and action = 'profile_mfa_mode_saved'
              order by created_at desc
              limit 1
            ) mfa_change on true
            left join lateral (
              select destination, expires_at
              from account_verification_codes
              where user_id = u.id
                and purpose = 'email_change'
                and consumed_at is null
                and expires_at > now()
              order by created_at desc
              limit 1
            ) email_change on true
            left join lateral (
              select destination, expires_at
              from account_verification_codes
              where user_id = u.id
                and purpose = 'password_change'
                and consumed_at is null
                and expires_at > now()
              order by created_at desc
              limit 1
            ) password_change on true
            where u.id = @user_id;
            """;
        command.Parameters.AddWithValue("user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ProfileRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.GetString(reader.GetOrdinal("username")).Trim(),
            reader.GetString(reader.GetOrdinal("display_name")).Trim(),
            reader.GetString(reader.GetOrdinal("status")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("email_verified_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("email_verified_at"))),
            NormalizeMfaMode(reader.GetString(reader.GetOrdinal("mfa_mode"))),
            reader.IsDBNull(reader.GetOrdinal("last_mfa_mode_changed_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("last_mfa_mode_changed_at"))),
            reader.IsDBNull(reader.GetOrdinal("previous_mfa_mode"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("previous_mfa_mode")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("pending_email_change_destination"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("pending_email_change_destination")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("pending_email_change_expires_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("pending_email_change_expires_at"))),
            reader.IsDBNull(reader.GetOrdinal("pending_password_change_destination"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("pending_password_change_destination")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("pending_password_change_expires_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("pending_password_change_expires_at"))));
    }

    private static async Task<VerificationRecord?> ReadActiveVerificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        string purpose,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              destination,
              code_hash,
              expires_at,
              failed_attempts,
              payload::text as payload
            from account_verification_codes
            where user_id = @user_id
              and purpose = @purpose
              and consumed_at is null
            order by created_at desc
            limit 1
            for update;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("purpose", purpose);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new VerificationRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.IsDBNull(reader.GetOrdinal("destination"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("destination")).Trim(),
            reader.GetString(reader.GetOrdinal("code_hash")).Trim(),
            CoerceTimestamp(reader.GetValue(reader.GetOrdinal("expires_at"))),
            reader.GetInt32(reader.GetOrdinal("failed_attempts")),
            reader.GetString(reader.GetOrdinal("payload")).Trim());
    }

    private static string ResolveDestination(string purpose, string currentEmail, string? destinationOverride)
    {
        if (string.Equals(purpose, EmailChangePurpose, StringComparison.Ordinal))
        {
            return destinationOverride?.Trim().ToLowerInvariant()
                ?? throw new InvalidOperationException("A destination email is required for email change verification.");
        }

        return currentEmail.Trim().ToLowerInvariant();
    }

    private static string ReadPayloadValue(string payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
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

    private static string NormalizeMfaMode(string mfaMode)
    {
        var normalized = mfaMode.Trim().ToLowerInvariant();
        return normalized switch
        {
            EmailCodeMfaMode => normalized,
            _ => NoMfaMode
        };
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

    private sealed record AccountRecord(
        Guid Id,
        string Email,
        string Username,
        string DisplayName,
        string Status,
        DateTimeOffset? EmailVerifiedAtUtc,
        string MfaMode);

    private sealed record ProfileRecord(
        Guid Id,
        string Email,
        string Username,
        string DisplayName,
        string Status,
        DateTimeOffset? EmailVerifiedAtUtc,
        string MfaMode,
        DateTimeOffset? LastMfaModeChangedAtUtc,
        string PreviousMfaMode,
        string PendingEmailChangeDestination,
        DateTimeOffset? PendingEmailChangeExpiresAtUtc,
        string PendingPasswordChangeDestination,
        DateTimeOffset? PendingPasswordChangeExpiresAtUtc);

    private sealed record VerificationRecord(
        Guid Id,
        string Destination,
        string CodeHash,
        DateTimeOffset ExpiresAtUtc,
        int FailedAttempts,
        string PayloadJson);
}
