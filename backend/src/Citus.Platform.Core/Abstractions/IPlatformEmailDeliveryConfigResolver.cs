namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// Resolves the effective SMTP configuration for the platform's mail
/// senders (verification notifications, invoice email). Fronts
/// <see cref="IPlatformSmtpConfigStore"/> with a short-lived cache so
/// the senders don't pay a database round-trip per outbound email,
/// and decrypts the protected password via
/// <see cref="IPlatformSecretProtector"/> so callers receive plaintext
/// the SMTP client can use directly.
///
/// Two read paths:
///   <see cref="GetCurrent"/> is sync against the in-memory cache and
///   is what the existing IPlatformVerificationNotificationSender
///   ProviderKey / GetConfigurationError sync members consume. Returns
///   null on cold cache.
///   <see cref="RefreshAsync"/> forces a fresh read from the store. Mail
///   senders call this on the send path so the very-first email after
///   process start still works without waiting for the next cache tick.
///
/// <see cref="Invalidate"/> drops the cache so the SysAdmin save
/// endpoint can force the next send to use the freshly-saved row.
/// </summary>
public interface IPlatformEmailDeliveryConfigResolver
{
    PlatformEmailDeliverySnapshot? GetCurrent();

    Task<PlatformEmailDeliverySnapshot?> RefreshAsync(CancellationToken cancellationToken);

    void Invalidate();
}

public sealed record PlatformEmailDeliverySnapshot(
    string Provider,
    string FromEmail,
    string FromDisplayName,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Password,
    DateTimeOffset LoadedAt)
{
    public string GetProviderKey() =>
        string.IsNullOrWhiteSpace(Provider) ? "disabled" : Provider.Trim().ToLowerInvariant();

    /// <summary>Same validation envelope the legacy
    /// PlatformEmailDeliveryOptions used so existing readers (notification
    /// readiness workflow) keep working unchanged.</summary>
    public string? GetConfigurationError()
    {
        var key = GetProviderKey();
        return key switch
        {
            "disabled" => "Platform notification provider is disabled.",
            "smtp" when string.IsNullOrWhiteSpace(FromEmail) => "Platform notification from-email is required.",
            "smtp" when string.IsNullOrWhiteSpace(Host) => "SMTP host is required.",
            "smtp" when Port <= 0 => "SMTP port must be greater than zero.",
            "smtp" when string.IsNullOrWhiteSpace(Username) => "SMTP username is required.",
            "smtp" when string.IsNullOrWhiteSpace(Password) => "SMTP password is required.",
            "smtp" => null,
            _ => $"Unsupported platform notification provider '{key}'."
        };
    }
}
