using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Platform.Core.Runtime;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed partial class PostgresPlatformAccountProfileRepository(
    PlatformPostgresConnectionFactory connectionFactory,
    IPlatformRuntimeStateRepository runtimeStateRepository,
    IPlatformVerificationNotificationSender notificationSender,
    IConfiguration configuration) : IPlatformAccountProfileRepository
{
    private const string EmailChangePurpose = "email_change";
    private const string PasswordChangePurpose = "password_change";
    private const string NoMfaMode = "none";
    private const string EmailCodeMfaMode = "email_code";
    private const string TotpAppMfaMode = "totp_app";
    private const string TotpPendingEnrollmentStatus = "pending";
    private const string TotpActiveEnrollmentStatus = "active";
    private const string TotpRevokedEnrollmentStatus = "revoked";
    private const string TotpEnrollmentIssuer = "Citus";
    private const string MfaRecoveryRequestedStatus = "requested";
    private const string MfaRecoveryApprovedStatus = "approved";
    private readonly PlatformTotpSecretProtector totpSecretProtector = new(configuration);

    public async Task<PlatformAccountProfileSummary?> GetAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await BuildSummaryAsync(connection, null, userId, cancellationToken);
    }

    public async Task<IReadOnlyList<PlatformMfaTimelineEntry>> GetMfaTimelineAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              al.action,
              al.payload::text as payload,
              al.created_at,
              al.actor_type,
              coalesce(sys_actor.display_name, sys_actor.email, '') as sysadmin_display_name,
              coalesce(nullif(user_actor.display_name, ''), nullif(user_actor.username, ''), user_actor.email, '') as user_display_name
            from audit_logs al
            left join sysadmin_accounts sys_actor
              on sys_actor.id = al.actor_id
             and al.actor_type = 'sysadmin'
            left join users user_actor
              on user_actor.id = al.actor_id
             and al.actor_type = 'user'
            where al.entity_type = 'platform_account'
              and al.entity_id = @user_id
              and al.action = any(@actions)
            order by al.created_at desc
            limit 20;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue(
            "actions",
            new[]
            {
                "profile_mfa_mode_saved",
                "account_totp_enrollment_started",
                "account_totp_enrollment_confirmed",
                "account_mfa_recovery_requested",
                "account_mfa_recovery_approved",
                "account_mfa_recovery_rejected",
                "account_mfa_recovery_executed",
                "account_mfa_reset"
            });

        var timeline = new List<PlatformMfaTimelineEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var payloadDocument = JsonDocument.Parse(reader.GetString(reader.GetOrdinal("payload")));
            var payload = payloadDocument.RootElement;
            var action = reader.GetString(reader.GetOrdinal("action")).Trim().ToLowerInvariant();
            var actorType = reader.GetString(reader.GetOrdinal("actor_type")).Trim().ToLowerInvariant();
            var actorDisplayName = actorType switch
            {
                "sysadmin" => reader.GetString(reader.GetOrdinal("sysadmin_display_name")).Trim(),
                _ => reader.GetString(reader.GetOrdinal("user_display_name")).Trim()
            };

            timeline.Add(new PlatformMfaTimelineEntry
            {
                Action = action,
                ActionLabel = PlatformAuditEvent.GetActionLabel(action),
                Detail = BuildMfaTimelineDetail(action, payload),
                Reason = ReadString(payload, "reason"),
                ActorType = actorType,
                ActorDisplayName = actorDisplayName,
                CreatedAtUtc = CoerceTimestamp(reader.GetValue(reader.GetOrdinal("created_at")))
            });
        }

        return timeline;
    }

    public async Task<PlatformTotpEnrollmentStartResult?> BeginTotpEnrollmentAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        await EnsureInteractiveWritesAllowedAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadAccountAsync(connection, transaction, userId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (string.Equals(current.MfaMode, TotpAppMfaMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Authenticator-app MFA is already enabled for this platform account.");
        }

        await RevokePendingTotpEnrollmentsAsync(connection, transaction, userId, cancellationToken);

        var enrollmentId = Guid.NewGuid();
        var secretBase32 = PlatformTotpAuthenticator.GenerateSecretBase32();
        var protectedSecretBase32 = totpSecretProtector.Protect(secretBase32);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var expiresAtUtc = startedAtUtc.AddMinutes(15);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                insert into account_mfa_totp_enrollments (
                  id,
                  user_id,
                  status,
                  secret_base32,
                  created_at,
                  expires_at
                )
                values (
                  @id,
                  @user_id,
                  @status,
                  @secret_base32,
                  @created_at,
                  @expires_at
                );
                """;
            command.Parameters.AddWithValue("id", enrollmentId);
            command.Parameters.AddWithValue("user_id", userId.Value);
            command.Parameters.AddWithValue("status", TotpPendingEnrollmentStatus);
            command.Parameters.AddWithValue("secret_base32", protectedSecretBase32);
            command.Parameters.AddWithValue("created_at", startedAtUtc);
            command.Parameters.AddWithValue("expires_at", expiresAtUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            "account_totp_enrollment_started",
            """
            jsonb_build_object(
              'enrollment_id', @enrollment_id,
              'mfa_mode', @mfa_mode,
              'status', @status,
              'expires_at_utc', @expires_at_utc
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("enrollment_id", enrollmentId);
                command.Parameters.AddWithValue("mfa_mode", TotpAppMfaMode);
                command.Parameters.AddWithValue("status", TotpPendingEnrollmentStatus);
                command.Parameters.AddWithValue("expires_at_utc", expiresAtUtc);
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var profile = await GetAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("Platform account was not found after starting TOTP enrollment.");
        var accountLabel = current.Email;

        return new PlatformTotpEnrollmentStartResult
        {
            EnrollmentId = enrollmentId,
            Issuer = TotpEnrollmentIssuer,
            AccountLabel = accountLabel,
            SecretBase32 = secretBase32,
            OtpAuthUri = PlatformTotpAuthenticator.CreateOtpAuthUri(TotpEnrollmentIssuer, accountLabel, secretBase32),
            ExpiresAtUtc = expiresAtUtc,
            Profile = profile
        };
    }

    public async Task<PlatformTotpEnrollmentConfirmationResult?> ConfirmTotpEnrollmentAsync(
        UserId userId,
        Guid enrollmentId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        await EnsureInteractiveWritesAllowedAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadAccountAsync(connection, transaction, userId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var enrollment = await ReadTotpEnrollmentAsync(connection, transaction, userId, enrollmentId, cancellationToken);
        if (enrollment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("TOTP enrollment session was not found.");
        }

        if (!string.Equals(enrollment.Status, TotpPendingEnrollmentStatus, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("TOTP enrollment is no longer pending confirmation.");
        }

        if (!enrollment.ExpiresAtUtc.HasValue || enrollment.ExpiresAtUtc.Value <= DateTimeOffset.UtcNow)
        {
            await RevokeTotpEnrollmentAsync(connection, transaction, enrollmentId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("TOTP enrollment session has expired. Start a new enrollment.");
        }

        if (!PlatformTotpAuthenticator.VerifyCode(enrollment.SecretBase32, verificationCode, DateTimeOffset.UtcNow))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Authenticator app code is invalid.");
        }

        var confirmedAtUtc = DateTimeOffset.UtcNow;
        await RevokeActiveTotpEnrollmentsAsync(connection, transaction, userId, enrollmentId, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update account_mfa_totp_enrollments
                set status = @status,
                    confirmed_at = @confirmed_at,
                    expires_at = null,
                    revoked_at = null
                where id = @id;
                """;
            command.Parameters.AddWithValue("id", enrollmentId);
            command.Parameters.AddWithValue("status", TotpActiveEnrollmentStatus);
            command.Parameters.AddWithValue("confirmed_at", confirmedAtUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update users
                set mfa_mode = @mfa_mode,
                    security_stamp = gen_random_uuid()::text,
                    updated_at = now()
                where id = @user_id;
                """;
            command.Parameters.AddWithValue("user_id", userId.Value);
            command.Parameters.AddWithValue("mfa_mode", TotpAppMfaMode);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            "account_totp_enrollment_confirmed",
            """
            jsonb_build_object(
              'enrollment_id', @enrollment_id,
              'previous_mfa_mode', @previous_mfa_mode,
              'mfa_mode', @mfa_mode,
              'status', @status
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("enrollment_id", enrollmentId);
                command.Parameters.AddWithValue("previous_mfa_mode", current.MfaMode);
                command.Parameters.AddWithValue("mfa_mode", TotpAppMfaMode);
                command.Parameters.AddWithValue("status", TotpActiveEnrollmentStatus);
            },
            cancellationToken);

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
                command.Parameters.AddWithValue("mfa_mode", TotpAppMfaMode);
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var profile = await GetAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("Platform account was not found after confirming TOTP enrollment.");

        return new PlatformTotpEnrollmentConfirmationResult
        {
            EnrollmentId = enrollmentId,
            ConfirmedAtUtc = confirmedAtUtc,
            Profile = profile
        };
    }

    public async Task<PlatformAccountProfileSummary?> SaveDisplayNameAsync(
        UserId userId,
        string displayName,
        CancellationToken cancellationToken)
    {
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
            command.Parameters.AddWithValue("user_id", userId.Value);
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
        UserId userId,
        string mfaMode,
        CancellationToken cancellationToken)
    {
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

        if (!string.Equals(mfaMode, TotpAppMfaMode, StringComparison.Ordinal))
        {
            await RevokeAllTotpEnrollmentsAsync(connection, transaction, userId, cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update users
                set mfa_mode = @mfa_mode,
                    security_stamp = gen_random_uuid()::text,
                    updated_at = now()
                where id = @user_id;
                """;
            command.Parameters.AddWithValue("user_id", userId.Value);
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

    public async Task<PlatformMfaRecoveryRequestResult?> RequestMfaRecoveryAsync(
        UserId userId,
        string reason,
        CancellationToken cancellationToken)
    {
        await EnsureInteractiveWritesAllowedAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadAccountAsync(connection, transaction, userId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (string.Equals(current.MfaMode, NoMfaMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MFA recovery request is not allowed because MFA is already disabled.");
        }

        var activeRequest = await ReadActiveMfaRecoveryRequestAsync(connection, transaction, userId, cancellationToken);
        if (activeRequest is not null)
        {
            throw new InvalidOperationException("An MFA recovery request is already pending review.");
        }

        var requestId = Guid.NewGuid();
        var requestedAtUtc = DateTimeOffset.UtcNow;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                insert into account_mfa_recovery_requests (
                  id,
                  user_id,
                  requested_by_user_id,
                  current_mfa_mode,
                  status,
                  request_reason,
                  requested_at
                )
                values (
                  @id,
                  @user_id,
                  @requested_by_user_id,
                  @current_mfa_mode,
                  @status,
                  @request_reason,
                  @requested_at
                );
                """;
            command.Parameters.AddWithValue("id", requestId);
            command.Parameters.AddWithValue("user_id", userId.Value);
            command.Parameters.AddWithValue("requested_by_user_id", userId.Value);
            command.Parameters.AddWithValue("current_mfa_mode", current.MfaMode);
            command.Parameters.AddWithValue("status", MfaRecoveryRequestedStatus);
            command.Parameters.AddWithValue("request_reason", reason);
            command.Parameters.AddWithValue("requested_at", requestedAtUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            "account_mfa_recovery_requested",
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
                command.Parameters.AddWithValue("current_mfa_mode", current.MfaMode);
                command.Parameters.AddWithValue("status", MfaRecoveryRequestedStatus);
                command.Parameters.AddWithValue("reason", reason);
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var profile = await GetAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("Platform account was not found after recording the MFA recovery request.");

        return new PlatformMfaRecoveryRequestResult
        {
            RequestId = requestId,
            Status = MfaRecoveryRequestedStatus,
            Reason = reason,
            RequestedAtUtc = requestedAtUtc,
            Profile = profile
        };
    }

    public Task<PlatformProfileChangeRequestResult?> RequestEmailChangeAsync(
        UserId userId,
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
        UserId userId,
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
        UserId userId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
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
            command.Parameters.AddWithValue("user_id", userId.Value);
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
        UserId userId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
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
            command.Parameters.AddWithValue("user_id", userId.Value);
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
        UserId userId,
        string purpose,
        string? destinationOverride,
        string payloadSql,
        string notificationType,
        CancellationToken cancellationToken,
        string? payloadValue = null)
    {
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
            insertVerification.Parameters.AddWithValue("user_id", userId.Value);
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
            insertDispatch.Parameters.AddWithValue("account_id", userId.Value);
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

    // Stage-1.4: cache + information_schema probe so the 9 ALTERs
    // here only fire on a fresh DB (or until the deploy-time migration
    // runner gets the same SQL). Same pattern as commit 2ef2640.
    private static volatile bool _schemaEnsured;

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await using (var probeConnection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        await using (var probe = probeConnection.CreateCommand())
        {
            probe.CommandText =
                """
                select count(*)
                from information_schema.columns
                where table_schema = 'public'
                  and table_name = 'account_verification_codes'
                  and column_name = 'payload';
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
              id char(7) primary key,
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
              user_id char(7) not null references users(id) on delete cascade,
              requested_by_user_id char(7) not null references users(id) on delete cascade,
              current_mfa_mode text not null,
              status text not null default 'requested',
              request_reason text not null,
              requested_at timestamptz not null default now(),
              review_reason text,
              reviewed_at timestamptz,
              reviewed_by_sysadmin_account_id char(7),
              execution_reason text,
              executed_at timestamptz,
              executed_by_sysadmin_account_id char(7),
              constraint account_mfa_recovery_requests_status_chk check (status in ('requested', 'approved', 'rejected', 'executed'))
            );

            alter table account_mfa_recovery_requests
              drop constraint if exists account_mfa_recovery_requests_current_mode_chk;

            alter table account_mfa_recovery_requests
              add constraint account_mfa_recovery_requests_current_mode_chk
              check (current_mfa_mode in ('none', 'email_code', 'totp_app'));

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

            create table if not exists account_verification_codes (
              id uuid primary key default gen_random_uuid(),
              user_id char(7) not null references users(id) on delete cascade,
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
              company_id char(7) null,
              actor_type text not null,
              actor_id char(7) null,
              entity_type text not null,
              entity_id uuid not null,
              action text not null,
              payload jsonb not null default '{}'::jsonb,
              created_at timestamptz not null default now()
            );

            create index if not exists idx_account_verification_codes_active
              on account_verification_codes (user_id, purpose, expires_at desc)
              where consumed_at is null;

            create index if not exists idx_account_mfa_recovery_requests_open
              on account_mfa_recovery_requests (user_id, status, requested_at desc)
              where status in ('requested', 'approved');

            create index if not exists idx_account_mfa_totp_enrollments_active
              on account_mfa_totp_enrollments (user_id, status, created_at desc)
              where status in ('pending', 'active');

            create index if not exists idx_platform_notification_dispatches_status
              on platform_notification_dispatches (status, created_at desc);

            create index if not exists idx_audit_logs_action_created_at
              on audit_logs (action, created_at desc);
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _schemaEnsured = true;
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
        UserId userId,
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
            LastMfaResetAtUtc = record.LastMfaResetAtUtc,
            LastMfaResetReason = record.LastMfaResetReason,
            LastMfaResetByDisplayName = record.LastMfaResetByDisplayName,
            ActiveTotpEnrollmentId = record.ActiveTotpEnrollmentId,
            ActiveTotpEnrollmentStartedAtUtc = record.ActiveTotpEnrollmentStartedAtUtc,
            ActiveTotpEnrollmentExpiresAtUtc = record.ActiveTotpEnrollmentExpiresAtUtc,
            ActiveMfaRecoveryRequestId = record.ActiveMfaRecoveryRequestId,
            ActiveMfaRecoveryStatus = record.ActiveMfaRecoveryStatus,
            ActiveMfaRecoveryRequestedAtUtc = record.ActiveMfaRecoveryRequestedAtUtc,
            ActiveMfaRecoveryRequestReason = record.ActiveMfaRecoveryRequestReason,
            ActiveMfaRecoveryReviewedAtUtc = record.ActiveMfaRecoveryReviewedAtUtc,
            ActiveMfaRecoveryReviewReason = record.ActiveMfaRecoveryReviewReason,
            ActiveMfaRecoveryReviewedByDisplayName = record.ActiveMfaRecoveryReviewedByDisplayName,
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
        UserId actorUserId,
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
        command.Parameters.AddWithValue("actor_id", actorUserId.Value);
        command.Parameters.AddWithValue("entity_id", actorUserId.Value);
        command.Parameters.AddWithValue("action", action);
        configureParameters(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task FinalizeDispatchAsSentAsync(
        NpgsqlConnection connection,
        Guid dispatchId,
        Guid requestAuditId,
        UserId actorUserId,
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
        UserId actorUserId,
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
        UserId actorUserId,
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
        command.Parameters.AddWithValue("actor_id", actorUserId.Value);
        command.Parameters.AddWithValue("entity_id", actorUserId.Value);
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
        UserId userId,
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
        command.Parameters.AddWithValue("user_id", userId.Value);
        var existing = await command.ExecuteScalarAsync(cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("That email address is already in use by another platform account.");
        }
    }

    private static async Task InvalidateActiveVerificationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
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
        command.Parameters.AddWithValue("user_id", userId.Value);
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
        UserId userId,
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
        command.Parameters.AddWithValue("user_id", userId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AccountRecord(
            UserId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.GetString(reader.GetOrdinal("username")).Trim(),
            reader.GetString(reader.GetOrdinal("display_name")).Trim(),
            reader.GetString(reader.GetOrdinal("status")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("email_verified_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("email_verified_at"))),
            NormalizeMfaMode(reader.GetString(reader.GetOrdinal("mfa_mode"))));
    }

    private static async Task<Guid?> ReadActiveMfaRecoveryRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
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
        command.Parameters.AddWithValue("user_id", userId.Value);

        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            Guid requestId => requestId,
            _ => null
        };
    }

    private async Task<TotpEnrollmentRecord?> ReadTotpEnrollmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
        Guid enrollmentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              status,
              secret_base32,
              created_at,
              expires_at,
              confirmed_at,
              revoked_at
            from account_mfa_totp_enrollments
            where id = @id
              and user_id = @user_id
            limit 1
            for update;
            """;
        command.Parameters.AddWithValue("id", enrollmentId);
        command.Parameters.AddWithValue("user_id", userId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TotpEnrollmentRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant(),
            totpSecretProtector.Unprotect(reader.GetString(reader.GetOrdinal("secret_base32")).Trim()),
            CoerceTimestamp(reader.GetValue(reader.GetOrdinal("created_at"))),
            reader.IsDBNull(reader.GetOrdinal("expires_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("expires_at"))),
            reader.IsDBNull(reader.GetOrdinal("confirmed_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("confirmed_at"))),
            reader.IsDBNull(reader.GetOrdinal("revoked_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("revoked_at"))));
    }

    private static async Task RevokePendingTotpEnrollmentsAsync(
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
            set status = @revoked_status,
                revoked_at = now()
            where user_id = @user_id
              and status = @pending_status;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("pending_status", TotpPendingEnrollmentStatus);
        command.Parameters.AddWithValue("revoked_status", TotpRevokedEnrollmentStatus);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RevokeActiveTotpEnrollmentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId userId,
        Guid keepEnrollmentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update account_mfa_totp_enrollments
            set status = @revoked_status,
                revoked_at = now()
            where user_id = @user_id
              and id <> @keep_enrollment_id
              and status in (@pending_status, @active_status);
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("keep_enrollment_id", keepEnrollmentId);
        command.Parameters.AddWithValue("pending_status", TotpPendingEnrollmentStatus);
        command.Parameters.AddWithValue("active_status", TotpActiveEnrollmentStatus);
        command.Parameters.AddWithValue("revoked_status", TotpRevokedEnrollmentStatus);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RevokeAllTotpEnrollmentsAsync(
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
            set status = @revoked_status,
                revoked_at = now()
            where user_id = @user_id
              and status in (@pending_status, @active_status);
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("pending_status", TotpPendingEnrollmentStatus);
        command.Parameters.AddWithValue("active_status", TotpActiveEnrollmentStatus);
        command.Parameters.AddWithValue("revoked_status", TotpRevokedEnrollmentStatus);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RevokeTotpEnrollmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid enrollmentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update account_mfa_totp_enrollments
            set status = @revoked_status,
                revoked_at = now()
            where id = @id;
            """;
        command.Parameters.AddWithValue("id", enrollmentId);
        command.Parameters.AddWithValue("revoked_status", TotpRevokedEnrollmentStatus);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ProfileRecord?> ReadProfileRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        UserId userId,
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
              mfa_reset.created_at as last_mfa_reset_at,
              coalesce(mfa_reset.reason, '') as last_mfa_reset_reason,
              coalesce(mfa_reset.actor_display_name, '') as last_mfa_reset_by_display_name,
              totp_enrollment.id as active_totp_enrollment_id,
              totp_enrollment.created_at as active_totp_enrollment_started_at,
              totp_enrollment.expires_at as active_totp_enrollment_expires_at,
              recovery_request.id as active_mfa_recovery_request_id,
              coalesce(recovery_request.status, '') as active_mfa_recovery_status,
              recovery_request.requested_at as active_mfa_recovery_requested_at,
              coalesce(recovery_request.request_reason, '') as active_mfa_recovery_request_reason,
              recovery_request.reviewed_at as active_mfa_recovery_reviewed_at,
              coalesce(recovery_request.review_reason, '') as active_mfa_recovery_review_reason,
              coalesce(recovery_request.reviewed_by_display_name, '') as active_mfa_recovery_reviewed_by_display_name,
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
              select
                al.created_at,
                coalesce(al.payload ->> 'reason', '') as reason,
                coalesce(sa.display_name, sa.email, 'SysAdmin') as actor_display_name
              from audit_logs al
              left join sysadmin_accounts sa on sa.id = al.actor_id
              where al.entity_type = 'platform_account'
                and al.entity_id = u.id
                and al.action = 'account_mfa_reset'
                and al.actor_type = 'sysadmin'
              order by al.created_at desc
              limit 1
            ) mfa_reset on true
            left join lateral (
              select
                e.id,
                e.created_at,
                e.expires_at
              from account_mfa_totp_enrollments e
              where e.user_id = u.id
                and e.status = 'pending'
                and e.revoked_at is null
                and e.expires_at > now()
              order by e.created_at desc
              limit 1
            ) totp_enrollment on true
            left join lateral (
              select
                r.id,
                r.status,
                r.request_reason,
                r.requested_at,
                r.review_reason,
                r.reviewed_at,
                coalesce(sa.display_name, sa.email, 'SysAdmin') as reviewed_by_display_name
              from account_mfa_recovery_requests r
              left join sysadmin_accounts sa on sa.id = r.reviewed_by_sysadmin_account_id
              where r.user_id = u.id
                and r.status in ('requested', 'approved')
              order by r.requested_at desc
              limit 1
            ) recovery_request on true
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
        command.Parameters.AddWithValue("user_id", userId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ProfileRecord(
            UserId.Parse(reader.GetString(reader.GetOrdinal("id"))),
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
            reader.IsDBNull(reader.GetOrdinal("last_mfa_reset_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("last_mfa_reset_at"))),
            reader.IsDBNull(reader.GetOrdinal("last_mfa_reset_reason"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("last_mfa_reset_reason")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("last_mfa_reset_by_display_name"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("last_mfa_reset_by_display_name")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("active_totp_enrollment_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("active_totp_enrollment_id")),
            reader.IsDBNull(reader.GetOrdinal("active_totp_enrollment_started_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("active_totp_enrollment_started_at"))),
            reader.IsDBNull(reader.GetOrdinal("active_totp_enrollment_expires_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("active_totp_enrollment_expires_at"))),
            reader.IsDBNull(reader.GetOrdinal("active_mfa_recovery_request_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("active_mfa_recovery_request_id")),
            reader.IsDBNull(reader.GetOrdinal("active_mfa_recovery_status"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("active_mfa_recovery_status")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("active_mfa_recovery_requested_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("active_mfa_recovery_requested_at"))),
            reader.IsDBNull(reader.GetOrdinal("active_mfa_recovery_request_reason"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("active_mfa_recovery_request_reason")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("active_mfa_recovery_reviewed_at"))
                ? null
                : CoerceTimestamp(reader.GetValue(reader.GetOrdinal("active_mfa_recovery_reviewed_at"))),
            reader.IsDBNull(reader.GetOrdinal("active_mfa_recovery_review_reason"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("active_mfa_recovery_review_reason")).Trim(),
            reader.IsDBNull(reader.GetOrdinal("active_mfa_recovery_reviewed_by_display_name"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("active_mfa_recovery_reviewed_by_display_name")).Trim(),
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
        UserId userId,
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
        command.Parameters.AddWithValue("user_id", userId.Value);
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
            TotpAppMfaMode => normalized,
            _ => NoMfaMode
        };
    }

    private static string BuildMfaTimelineDetail(string action, JsonElement payload) =>
        action switch
        {
            "profile_mfa_mode_saved" => BuildTransitionDetail(
                ReadString(payload, "previous_mfa_mode"),
                ReadString(payload, "mfa_mode")),
            "account_totp_enrollment_started" => BuildTotpEnrollmentDetail(payload),
            "account_totp_enrollment_confirmed" => BuildTotpEnrollmentDetail(payload),
            "account_mfa_recovery_requested" => BuildMfaRecoveryTimelineDetail(payload),
            "account_mfa_recovery_approved" => BuildMfaRecoveryTimelineDetail(payload),
            "account_mfa_recovery_rejected" => BuildMfaRecoveryTimelineDetail(payload),
            "account_mfa_recovery_executed" => BuildResetTimelineDetail(payload),
            "account_mfa_reset" => BuildResetTimelineDetail(payload),
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

    private static string BuildMfaRecoveryTimelineDetail(JsonElement payload)
    {
        var currentMfaMode = ReadString(payload, "current_mfa_mode");
        var status = ReadString(payload, "status");
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(currentMfaMode))
        {
            parts.Add(currentMfaMode);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            parts.Add(status);
        }

        return string.Join(" | ", parts);
    }

    private static string BuildTotpEnrollmentDetail(JsonElement payload)
    {
        var parts = new List<string>();
        AddIfPresent(parts, ReadString(payload, "mfa_mode"));
        AddIfPresent(parts, ReadString(payload, "status"));
        AddIfPresent(parts, ReadString(payload, "expires_at_utc"));
        return string.Join(" | ", parts);
    }

    private static string BuildResetTimelineDetail(JsonElement payload)
    {
        var detail = BuildTransitionDetail(
            ReadString(payload, "previous_mfa_mode"),
            ReadString(payload, "mfa_mode"));
        var revokedChallengeCount = ReadString(payload, "revoked_challenge_count");
        if (string.IsNullOrWhiteSpace(revokedChallengeCount))
        {
            return detail;
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"revoked challenges: {revokedChallengeCount}"
            : $"{detail} | revoked challenges: {revokedChallengeCount}";
    }

    private static string ReadString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.ToString().Trim(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => string.Empty
        };
    }

    private static void AddIfPresent(List<string> segments, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            segments.Add(value);
        }
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
        UserId Id,
        string Email,
        string Username,
        string DisplayName,
        string Status,
        DateTimeOffset? EmailVerifiedAtUtc,
        string MfaMode);

    private sealed record ProfileRecord(
        UserId Id,
        string Email,
        string Username,
        string DisplayName,
        string Status,
        DateTimeOffset? EmailVerifiedAtUtc,
        string MfaMode,
        DateTimeOffset? LastMfaModeChangedAtUtc,
        string PreviousMfaMode,
        DateTimeOffset? LastMfaResetAtUtc,
        string LastMfaResetReason,
        string LastMfaResetByDisplayName,
        Guid? ActiveTotpEnrollmentId,
        DateTimeOffset? ActiveTotpEnrollmentStartedAtUtc,
        DateTimeOffset? ActiveTotpEnrollmentExpiresAtUtc,
        Guid? ActiveMfaRecoveryRequestId,
        string ActiveMfaRecoveryStatus,
        DateTimeOffset? ActiveMfaRecoveryRequestedAtUtc,
        string ActiveMfaRecoveryRequestReason,
        DateTimeOffset? ActiveMfaRecoveryReviewedAtUtc,
        string ActiveMfaRecoveryReviewReason,
        string ActiveMfaRecoveryReviewedByDisplayName,
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

    private sealed record TotpEnrollmentRecord(
        Guid Id,
        string Status,
        string SecretBase32,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        DateTimeOffset? ConfirmedAtUtc,
        DateTimeOffset? RevokedAtUtc);
}
