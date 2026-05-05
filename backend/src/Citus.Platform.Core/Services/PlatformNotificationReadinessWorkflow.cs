using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Services;

public sealed class PlatformNotificationReadinessWorkflow(
    IPlatformRuntimeStateRepository runtimeStateRepository,
    IPlatformVerificationNotificationSender notificationSender) : IPlatformNotificationReadinessWorkflow
{
    public async Task<PlatformNotificationReadinessReport> GetAsync(CancellationToken cancellationToken)
    {
        var state = await runtimeStateRepository.GetNotificationReadinessStateAsync(cancellationToken);
        return BuildReport(state, notificationSender.GetConfigurationError());
    }

    public async Task<PlatformNotificationTestSendResult> SendTestAsync(
        string destination,
        string recipientDisplayName,
        CancellationToken cancellationToken)
    {
        var normalizedDestination = NormalizeDestination(destination);
        var normalizedRecipient = NormalizeRecipient(recipientDisplayName, normalizedDestination);
        var configurationError = notificationSender.GetConfigurationError();
        var currentState = await runtimeStateRepository.GetNotificationReadinessStateAsync(cancellationToken);
        var configPresent = string.IsNullOrWhiteSpace(configurationError);
        var testedAtUtc = DateTimeOffset.UtcNow;

        if (!configPresent)
        {
            var failedState = await runtimeStateRepository.UpsertNotificationReadinessStateAsync(
                new PlatformNotificationReadinessState
                {
                    ConfigPresent = false,
                    TestStatus = "failed",
                    LastTestedAtUtc = testedAtUtc,
                    VerificationReady = currentState?.VerificationReady ?? false
                },
                cancellationToken);

            return new PlatformNotificationTestSendResult
            {
                Succeeded = false,
                ProviderKey = notificationSender.ProviderKey,
                Destination = normalizedDestination,
                FailureMessage = configurationError ?? "Platform notification provider is not configured.",
                Readiness = BuildReport(failedState, configurationError)
            };
        }

        var sendResult = await notificationSender.SendVerificationAsync(
            new PlatformVerificationNotificationMessage
            {
                DispatchId = Guid.NewGuid(),
                UserId = default,
                Purpose = "notification_test",
                Destination = normalizedDestination,
                RecipientDisplayName = normalizedRecipient,
                VerificationCode = CreateTestCode(),
                ExpiresAtUtc = testedAtUtc.AddMinutes(10)
            },
            cancellationToken);

        var updatedState = await runtimeStateRepository.UpsertNotificationReadinessStateAsync(
            new PlatformNotificationReadinessState
            {
                ConfigPresent = true,
                TestStatus = sendResult.Succeeded ? "passed" : "failed",
                LastTestedAtUtc = testedAtUtc,
                VerificationReady = currentState?.VerificationReady ?? false
            },
            cancellationToken);

        return new PlatformNotificationTestSendResult
        {
            Succeeded = sendResult.Succeeded,
            ProviderKey = sendResult.ProviderKey,
            Destination = normalizedDestination,
            FailureMessage = sendResult.FailureMessage,
            Readiness = BuildReport(updatedState, notificationSender.GetConfigurationError())
        };
    }

    private static PlatformNotificationReadinessReport BuildReport(
        PlatformNotificationReadinessState? state,
        string? configurationError)
    {
        var normalizedState = state ?? new PlatformNotificationReadinessState
        {
            ConfigPresent = false,
            TestStatus = "untested",
            VerificationReady = false
        };

        var configurationIssue = configurationError?.Trim() ?? string.Empty;
        var ready = normalizedState.IsVerificationDeliveryReady && string.IsNullOrWhiteSpace(configurationIssue);
        var blockingReason = ready
            ? string.Empty
            : !string.IsNullOrWhiteSpace(configurationIssue) && normalizedState.IsVerificationDeliveryReady
                ? configurationIssue
                : normalizedState.GetBlockingReason();

        if (string.IsNullOrWhiteSpace(blockingReason) && !ready)
        {
            blockingReason = string.IsNullOrWhiteSpace(configurationIssue)
                ? "Notification readiness has not been configured."
                : configurationIssue;
        }

        return new PlatformNotificationReadinessReport
        {
            ConfigPresent = normalizedState.ConfigPresent,
            TestStatus = normalizedState.TestStatus,
            LastTestedAtUtc = normalizedState.LastTestedAtUtc,
            VerificationReady = normalizedState.VerificationReady,
            IsVerificationDeliveryReady = ready,
            BlockingReason = blockingReason,
            ConfigurationError = configurationIssue
        };
    }

    private static string NormalizeDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new InvalidOperationException("A destination email is required for notification test send.");
        }

        return destination.Trim().ToLowerInvariant();
    }

    private static string NormalizeRecipient(string recipientDisplayName, string fallbackDestination) =>
        string.IsNullOrWhiteSpace(recipientDisplayName)
            ? fallbackDestination
            : recipientDisplayName.Trim();

    private static string CreateTestCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = Guid.NewGuid().ToString("N").ToUpperInvariant();
        Span<char> buffer = stackalloc char[6];
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = alphabet[random[index] % alphabet.Length];
        }

        return new string(buffer);
    }
}
