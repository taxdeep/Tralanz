namespace Citus.Platform.Infrastructure.Notifications;

public sealed class PlatformEmailDeliveryOptions
{
    public const string SectionName = "PlatformNotifications";

    public string Provider { get; set; } = "disabled";

    public string FromEmail { get; set; } = string.Empty;

    public string FromDisplayName { get; set; } = "Citus";

    public SmtpOptions Smtp { get; set; } = new();

    public string? GetConfigurationError()
    {
        var provider = Normalize(Provider);
        return provider switch
        {
            "disabled" => "Platform notification provider is disabled.",
            "smtp" => Smtp.GetConfigurationError(FromEmail),
            _ => $"Unsupported platform notification provider '{provider}'."
        };
    }

    public string GetProviderKey() => Normalize(Provider);

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "disabled" : value.Trim().ToLowerInvariant();

    public sealed class SmtpOptions
    {
        public string Host { get; set; } = string.Empty;

        public int Port { get; set; } = 587;

        public bool UseSsl { get; set; } = true;

        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public string? GetConfigurationError(string fromEmail)
        {
            if (string.IsNullOrWhiteSpace(fromEmail))
            {
                return "Platform notification from-email is required.";
            }

            if (string.IsNullOrWhiteSpace(Host))
            {
                return "SMTP host is required.";
            }

            if (Port <= 0)
            {
                return "SMTP port must be greater than zero.";
            }

            if (string.IsNullOrWhiteSpace(Username))
            {
                return "SMTP username is required.";
            }

            if (string.IsNullOrWhiteSpace(Password))
            {
                return "SMTP password is required.";
            }

            return null;
        }
    }
}
