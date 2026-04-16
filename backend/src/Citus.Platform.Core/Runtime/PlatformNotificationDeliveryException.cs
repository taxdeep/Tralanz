namespace Citus.Platform.Core.Runtime;

public sealed class PlatformNotificationDeliveryException(string message) : InvalidOperationException(message);
