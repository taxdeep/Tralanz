using Citus.Platform.Infrastructure.Notifications;

namespace Citus.SysAdmin.Api.Tests;

public sealed class PlatformNotificationOptionsTests
{
    [Fact]
    public void GetConfigurationError_ReturnsDisabled_WhenProviderIsDisabled()
    {
        var options = new PlatformEmailDeliveryOptions
        {
            Provider = "disabled"
        };

        Assert.Equal("Platform notification provider is disabled.", options.GetConfigurationError());
    }

    [Fact]
    public void GetConfigurationError_ReturnsNull_WhenSmtpConfigurationIsComplete()
    {
        var options = new PlatformEmailDeliveryOptions
        {
            Provider = "smtp",
            FromEmail = "noreply@example.test",
            Smtp = new PlatformEmailDeliveryOptions.SmtpOptions
            {
                Host = "smtp.example.test",
                Port = 587,
                Username = "mailer",
                Password = "secret"
            }
        };

        Assert.Null(options.GetConfigurationError());
    }

    [Fact]
    public void GetConfigurationError_ReturnsSpecificMessage_WhenSmtpHostMissing()
    {
        var options = new PlatformEmailDeliveryOptions
        {
            Provider = "smtp",
            FromEmail = "noreply@example.test",
            Smtp = new PlatformEmailDeliveryOptions.SmtpOptions
            {
                Host = "",
                Port = 587,
                Username = "mailer",
                Password = "secret"
            }
        };

        Assert.Equal("SMTP host is required.", options.GetConfigurationError());
    }
}
