using System.Net;
using System.Net.Mail;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Citus.Platform.Infrastructure.Notifications;

/// <summary>
/// SMTP sender for platform verification notifications (sysadmin
/// password resets, MFA codes, business sign-in MFA). Reads SMTP
/// configuration from <see cref="IPlatformEmailDeliveryConfigResolver"/>
/// — the underlying platform_smtp_config row, not appsettings.
///
/// IPlatformVerificationNotificationSender's sync ProviderKey /
/// GetConfigurationError surface reads against the resolver's cached
/// snapshot. Send paths are async and call RefreshAsync first so the
/// freshest config wins on every actual send.
/// </summary>
public sealed class SmtpPlatformVerificationNotificationSender(
    IPlatformEmailDeliveryConfigResolver configResolver,
    ILogger<SmtpPlatformVerificationNotificationSender> logger) : IPlatformVerificationNotificationSender
{
    public string ProviderKey => configResolver.GetCurrent()?.GetProviderKey() ?? "disabled";

    public string? GetConfigurationError() =>
        configResolver.GetCurrent()?.GetConfigurationError()
            ?? "Platform notification provider has not been configured yet.";

    public async Task<PlatformNotificationSendResult> SendVerificationAsync(
        PlatformVerificationNotificationMessage message,
        CancellationToken cancellationToken) =>
        await SendInternalAsync(
            message.DispatchId,
            message.Destination,
            ResolveRecipientDisplayName(message.Destination, message.RecipientDisplayName),
            BuildSubject(message.Purpose),
            BuildBody(message),
            cancellationToken).ConfigureAwait(false);

    public Task<PlatformNotificationSendResult> SendPasswordResetAsync(
        PasswordResetNotificationMessage message,
        CancellationToken cancellationToken) =>
        SendVerificationAsync(
            new PlatformVerificationNotificationMessage
            {
                DispatchId = message.DispatchId,
                UserId = Guid.Empty,
                Purpose = "password_reset",
                Destination = message.Destination,
                RecipientDisplayName = message.RecipientDisplayName,
                VerificationCode = message.VerificationCode,
                ExpiresAtUtc = message.ExpiresAtUtc
            },
            cancellationToken);

    private async Task<PlatformNotificationSendResult> SendInternalAsync(
        Guid dispatchId,
        string destination,
        string recipientDisplayName,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        var snapshot = await configResolver.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new PlatformNotificationSendResult
            {
                Succeeded = false,
                ProviderKey = "disabled",
                FailureMessage = "Platform notification provider has not been configured yet."
            };
        }

        var configurationError = snapshot.GetConfigurationError();
        if (!string.IsNullOrWhiteSpace(configurationError))
        {
            return new PlatformNotificationSendResult
            {
                Succeeded = false,
                ProviderKey = snapshot.GetProviderKey(),
                FailureMessage = configurationError
            };
        }

        using var mailMessage = new MailMessage
        {
            From = new MailAddress(snapshot.FromEmail.Trim(), snapshot.FromDisplayName.Trim()),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        mailMessage.To.Add(new MailAddress(destination.Trim(), recipientDisplayName));

        using var client = new SmtpClient(snapshot.Host.Trim(), snapshot.Port)
        {
            EnableSsl = snapshot.UseSsl,
            Credentials = new NetworkCredential(snapshot.Username.Trim(), snapshot.Password)
        };

        try
        {
            await client.SendMailAsync(mailMessage, cancellationToken).ConfigureAwait(false);
            return new PlatformNotificationSendResult
            {
                Succeeded = true,
                ProviderKey = snapshot.GetProviderKey()
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to send platform verification notification dispatch {DispatchId} to {Destination}.",
                dispatchId,
                destination);

            return new PlatformNotificationSendResult
            {
                Succeeded = false,
                ProviderKey = snapshot.GetProviderKey(),
                FailureMessage = ex.Message
            };
        }
    }

    private static string ResolveRecipientDisplayName(string destination, string recipientDisplayName) =>
        string.IsNullOrWhiteSpace(recipientDisplayName)
            ? destination.Trim()
            : recipientDisplayName.Trim();

    private static string BuildSubject(string purpose) =>
        purpose.Trim().ToLowerInvariant() switch
        {
            "notification_test" => "Tralanz Books notification test",
            "business_sign_in_mfa" => "Tralanz Books sign-in verification",
            "email_change" => "Tralanz Books email change verification",
            "password_change" => "Tralanz Books password change verification",
            "password_reset" => "Tralanz Books password reset verification",
            _ => "Tralanz Books verification code"
        };

    private static string BuildBody(PlatformVerificationNotificationMessage message)
    {
        var recipient = ResolveRecipientDisplayName(message.Destination, message.RecipientDisplayName);
        var bodyLead = message.Purpose.Trim().ToLowerInvariant() switch
        {
            "notification_test" =>
                "This is a platform notification test for your Tralanz Books environment.",
            "business_sign_in_mfa" =>
                "A business sign-in verification was requested for your Tralanz Books account.",
            "email_change" =>
                $"Confirm the new email address for your Tralanz Books account: {message.Destination.Trim()}.",
            "password_change" =>
                "A password change confirmation was requested for your Tralanz Books account.",
            "password_reset" =>
                "A Tralanz SysAdmin password reset has been requested for your platform account.",
            _ =>
                "A Tralanz Books verification code was requested for your platform account."
        };

        return
            $"""
            Hello {recipient},

            {bodyLead}

            Verification code: {message.VerificationCode}
            Expires at (UTC): {message.ExpiresAtUtc:yyyy-MM-dd HH:mm:ss}

            If you did not request this change, contact your platform operator immediately.
            """;
    }
}
