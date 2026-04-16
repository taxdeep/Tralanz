using System.Net;
using System.Net.Mail;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Citus.Platform.Infrastructure.Notifications;

public sealed class SmtpPlatformVerificationNotificationSender(
    IOptions<PlatformEmailDeliveryOptions> options,
    ILogger<SmtpPlatformVerificationNotificationSender> logger) : IPlatformVerificationNotificationSender
{
    public string ProviderKey => options.Value.GetProviderKey();

    public string? GetConfigurationError() => options.Value.GetConfigurationError();

    public Task<PlatformNotificationSendResult> SendVerificationAsync(
        PlatformVerificationNotificationMessage message,
        CancellationToken cancellationToken) =>
        SendInternalAsync(
            message.DispatchId,
            message.Destination,
            ResolveRecipientDisplayName(message.Destination, message.RecipientDisplayName),
            BuildSubject(message.Purpose),
            BuildBody(message),
            cancellationToken);

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
        var configurationError = GetConfigurationError();
        if (!string.IsNullOrWhiteSpace(configurationError))
        {
            return new PlatformNotificationSendResult
            {
                Succeeded = false,
                ProviderKey = ProviderKey,
                FailureMessage = configurationError
            };
        }

        var current = options.Value;
        var smtp = current.Smtp;
        using var mailMessage = new MailMessage
        {
            From = new MailAddress(current.FromEmail.Trim(), current.FromDisplayName.Trim()),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        mailMessage.To.Add(new MailAddress(destination.Trim(), recipientDisplayName));

        using var client = new SmtpClient(smtp.Host.Trim(), smtp.Port)
        {
            EnableSsl = smtp.UseSsl,
            Credentials = new NetworkCredential(smtp.Username.Trim(), smtp.Password)
        };

        try
        {
            await client.SendMailAsync(mailMessage, cancellationToken);
            return new PlatformNotificationSendResult
            {
                Succeeded = true,
                ProviderKey = ProviderKey
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
                ProviderKey = ProviderKey,
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
            "notification_test" => "Citus notification test",
            "email_change" => "Citus email change verification",
            "password_change" => "Citus password change verification",
            "password_reset" => "Citus password reset verification",
            _ => "Citus verification code"
        };

    private static string BuildBody(PlatformVerificationNotificationMessage message)
    {
        var recipient = ResolveRecipientDisplayName(message.Destination, message.RecipientDisplayName);
        var bodyLead = message.Purpose.Trim().ToLowerInvariant() switch
        {
            "notification_test" =>
                "This is a platform notification test for your Citus environment.",
            "email_change" =>
                $"Confirm the new email address for your Citus account: {message.Destination.Trim()}.",
            "password_change" =>
                "A password change confirmation was requested for your Citus account.",
            "password_reset" =>
                "A Citus SysAdmin password reset has been requested for your platform account.",
            _ =>
                "A Citus verification code was requested for your platform account."
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
