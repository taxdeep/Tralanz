using System.Net.Mail;
using System.Linq;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Accounts;

public sealed class PlatformAccountProfileWorkflow(
    IPlatformAccountProfileRepository repository,
    SysAdminPasswordHasher passwordHasher) : IPlatformAccountProfileWorkflow
{
    public Task<PlatformAccountProfileSummary?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        return repository.GetAsync(userId, cancellationToken);
    }

    public Task<IReadOnlyList<PlatformMfaTimelineEntry>> GetMfaTimelineAsync(Guid userId, CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        return repository.GetMfaTimelineAsync(userId, cancellationToken);
    }

    public Task<PlatformTotpEnrollmentStartResult?> BeginTotpEnrollmentAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        return repository.BeginTotpEnrollmentAsync(userId, cancellationToken);
    }

    public Task<PlatformTotpEnrollmentConfirmationResult?> ConfirmTotpEnrollmentAsync(
        Guid userId,
        Guid enrollmentId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        if (enrollmentId == Guid.Empty)
        {
            throw new InvalidOperationException("TOTP enrollment id is required.");
        }

        return repository.ConfirmTotpEnrollmentAsync(
            userId,
            enrollmentId,
            NormalizeTotpVerificationCode(verificationCode),
            cancellationToken);
    }

    public Task<PlatformAccountProfileSummary?> SaveDisplayNameAsync(
        Guid userId,
        string displayName,
        CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        return repository.SaveDisplayNameAsync(
            userId,
            NormalizeDisplayName(displayName),
            cancellationToken);
    }

    public Task<PlatformAccountProfileSummary?> SaveMfaModeAsync(
        Guid userId,
        string mfaMode,
        CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        return repository.SaveMfaModeAsync(
            userId,
            NormalizeMfaMode(mfaMode),
            cancellationToken);
    }

    public Task<PlatformMfaRecoveryRequestResult?> RequestMfaRecoveryAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        return repository.RequestMfaRecoveryAsync(
            userId,
            NormalizeRecoveryReason(reason),
            cancellationToken);
    }

    public Task<PlatformProfileChangeRequestResult?> RequestEmailChangeAsync(
        Guid userId,
        string newEmail,
        CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        return repository.RequestEmailChangeAsync(
            userId,
            NormalizeEmail(newEmail),
            cancellationToken);
    }

    public Task<PlatformProfileChangeRequestResult?> RequestPasswordChangeAsync(
        Guid userId,
        string newPassword,
        CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        var normalizedPassword = NormalizePassword(newPassword);
        return repository.RequestPasswordChangeAsync(
            userId,
            passwordHasher.HashPassword(normalizedPassword),
            cancellationToken);
    }

    public Task<PlatformProfileChangeConfirmationResult?> ConfirmEmailChangeAsync(
        Guid userId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        return repository.ConfirmEmailChangeAsync(
            userId,
            NormalizeVerificationCode(verificationCode),
            cancellationToken);
    }

    public Task<PlatformProfileChangeConfirmationResult?> ConfirmPasswordChangeAsync(
        Guid userId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        EnsureUserId(userId);
        return repository.ConfirmPasswordChangeAsync(
            userId,
            NormalizeVerificationCode(verificationCode),
            cancellationToken);
    }

    private static void EnsureUserId(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new InvalidOperationException("Platform account id is required.");
        }
    }

    private static string NormalizeDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Display name is required.");
        }

        var normalized = displayName.Trim();
        if (normalized.Length > 240)
        {
            throw new InvalidOperationException("Display name must be 240 characters or fewer.");
        }

        return normalized;
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Email address is required.");
        }

        var normalized = email.Trim().ToLowerInvariant();

        try
        {
            normalized = new MailAddress(normalized).Address.Trim().ToLowerInvariant();
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("A valid email address is required.");
        }

        if (normalized.Length > 320)
        {
            throw new InvalidOperationException("Email address must be 320 characters or fewer.");
        }

        return normalized;
    }

    private static string NormalizePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password is required.");
        }

        var normalized = password.Trim();
        if (normalized.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }

        if (normalized.Length > 128)
        {
            throw new InvalidOperationException("Password must be 128 characters or fewer.");
        }

        return normalized;
    }

    private static string NormalizeVerificationCode(string verificationCode)
    {
        if (string.IsNullOrWhiteSpace(verificationCode))
        {
            throw new InvalidOperationException("Verification code is required.");
        }

        var normalized = verificationCode.Trim().ToUpperInvariant();
        if (normalized.Length != 6)
        {
            throw new InvalidOperationException("Verification code must be 6 characters.");
        }

        return normalized;
    }

    private static string NormalizeRecoveryReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("Recovery reason is required.");
        }

        var normalized = reason.Trim();
        if (normalized.Length > 400)
        {
            throw new InvalidOperationException("Recovery reason must be 400 characters or fewer.");
        }

        return normalized;
    }

    private static string NormalizeMfaMode(string mfaMode)
    {
        if (string.IsNullOrWhiteSpace(mfaMode))
        {
            throw new InvalidOperationException("MFA mode is required.");
        }

        var normalized = mfaMode.Trim().ToLowerInvariant();
        return normalized switch
        {
            "none" => normalized,
            "email_code" => normalized,
            "totp_app" => throw new InvalidOperationException("Use the TOTP enrollment flow before enabling authenticator-app MFA."),
            _ => throw new InvalidOperationException("Unsupported MFA mode.")
        };
    }

    private static string NormalizeTotpVerificationCode(string verificationCode)
    {
        if (string.IsNullOrWhiteSpace(verificationCode))
        {
            throw new InvalidOperationException("Authenticator app code is required.");
        }

        var normalized = verificationCode.Trim();
        if (normalized.Length != PlatformTotpAuthenticator.Digits || !normalized.All(char.IsDigit))
        {
            throw new InvalidOperationException($"Authenticator app code must be {PlatformTotpAuthenticator.Digits} digits.");
        }

        return normalized;
    }
}
