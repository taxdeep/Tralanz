using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Citus.Platform.Core.Services;
using Xunit;

namespace Citus.Platform.Core.Tests;

public sealed class PlatformNotificationReadinessWorkflowTests
{
    [Fact]
    public async Task GetAsync_ReportsConfigurationError_WhenPersistedStateLooksReady()
    {
        var runtimeRepository = new InMemoryPlatformRuntimeStateRepository
        {
            NotificationState = new PlatformNotificationReadinessState
            {
                ConfigPresent = true,
                TestStatus = "passed",
                VerificationReady = true
            }
        };
        var sender = new FakeNotificationSender
        {
            ConfigurationError = "SMTP host is required."
        };
        var workflow = new PlatformNotificationReadinessWorkflow(runtimeRepository, sender);

        var result = await workflow.GetAsync(CancellationToken.None);

        Assert.False(result.IsVerificationDeliveryReady);
        Assert.Equal("SMTP host is required.", result.BlockingReason);
        Assert.Equal("SMTP host is required.", result.ConfigurationError);
    }

    [Fact]
    public async Task SendTestAsync_MarksTestPassed_WhenProviderSendSucceeds()
    {
        var runtimeRepository = new InMemoryPlatformRuntimeStateRepository
        {
            NotificationState = new PlatformNotificationReadinessState
            {
                ConfigPresent = false,
                TestStatus = "untested",
                VerificationReady = true
            }
        };
        var sender = new FakeNotificationSender
        {
            SendResult = new PlatformNotificationSendResult
            {
                Succeeded = true,
                ProviderKey = "smtp"
            }
        };
        var workflow = new PlatformNotificationReadinessWorkflow(runtimeRepository, sender);

        var result = await workflow.SendTestAsync("ops@example.test", "Ops", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("ops@example.test", result.Destination);
        Assert.NotNull(sender.LastMessage);
        Assert.Equal("notification_test", sender.LastMessage!.Purpose);
        Assert.Equal("ops@example.test", sender.LastMessage.Destination);
        Assert.Equal("passed", runtimeRepository.SavedState!.TestStatus);
        Assert.True(runtimeRepository.SavedState.ConfigPresent);
        Assert.True(runtimeRepository.SavedState.VerificationReady);
        Assert.Equal("passed", result.Readiness.TestStatus);
    }

    [Fact]
    public async Task SendTestAsync_MarksTestFailed_WhenConfigurationIsInvalid()
    {
        var runtimeRepository = new InMemoryPlatformRuntimeStateRepository
        {
            NotificationState = new PlatformNotificationReadinessState
            {
                ConfigPresent = true,
                TestStatus = "passed",
                VerificationReady = false
            }
        };
        var sender = new FakeNotificationSender
        {
            ConfigurationError = "Platform notification provider is disabled."
        };
        var workflow = new PlatformNotificationReadinessWorkflow(runtimeRepository, sender);

        var result = await workflow.SendTestAsync("ops@example.test", "Ops", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Platform notification provider is disabled.", result.FailureMessage);
        Assert.Null(sender.LastMessage);
        Assert.NotNull(runtimeRepository.SavedState);
        Assert.False(runtimeRepository.SavedState!.ConfigPresent);
        Assert.Equal("failed", runtimeRepository.SavedState.TestStatus);
        Assert.False(runtimeRepository.SavedState.VerificationReady);
        Assert.Equal("failed", result.Readiness.TestStatus);
    }

    private sealed class InMemoryPlatformRuntimeStateRepository : IPlatformRuntimeStateRepository
    {
        public PlatformNotificationReadinessState? NotificationState { get; set; }

        public PlatformNotificationReadinessState? SavedState { get; private set; }

        public PlatformFirstCompanySetupState? FirstCompanySetupState { get; set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlatformMaintenanceState?> GetMaintenanceStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<PlatformMaintenanceState?>(null);

        public Task<PlatformMaintenanceState> UpsertMaintenanceStateAsync(
            PlatformMaintenanceState state,
            CancellationToken cancellationToken) =>
            Task.FromResult(state);

        public Task<PlatformNotificationReadinessState?> GetNotificationReadinessStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NotificationState);

        public Task<PlatformNotificationReadinessState> UpsertNotificationReadinessStateAsync(
            PlatformNotificationReadinessState state,
            CancellationToken cancellationToken)
        {
            SavedState = state;
            NotificationState = state;
            return Task.FromResult(state);
        }

        public Task<PlatformFirstCompanySetupState?> GetFirstCompanySetupStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(FirstCompanySetupState);

        public Task<PlatformFirstCompanySetupState> UpsertFirstCompanySetupStateAsync(
            PlatformFirstCompanySetupState state,
            CancellationToken cancellationToken) =>
            Task.FromResult(FirstCompanySetupState = state);
    }

    private sealed class FakeNotificationSender : IPlatformVerificationNotificationSender
    {
        public string ProviderKey { get; init; } = "smtp";

        public string? ConfigurationError { get; init; }

        public PlatformNotificationSendResult SendResult { get; init; } = new()
        {
            Succeeded = true,
            ProviderKey = "smtp"
        };

        public PlatformVerificationNotificationMessage? LastMessage { get; private set; }

        public string? GetConfigurationError() => ConfigurationError;

        public Task<PlatformNotificationSendResult> SendVerificationAsync(
            PlatformVerificationNotificationMessage message,
            CancellationToken cancellationToken)
        {
            LastMessage = message;
            return Task.FromResult(SendResult);
        }

        public Task<PlatformNotificationSendResult> SendPasswordResetAsync(
            PasswordResetNotificationMessage message,
            CancellationToken cancellationToken) =>
            Task.FromResult(SendResult);
    }
}
