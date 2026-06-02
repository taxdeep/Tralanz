using Modules.Company.FeatureManagement;

namespace Tests.Company;

public sealed class CompanyModuleFlagWorkflowTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly UserId ActorUserId = UserId.FromOrdinal(1);

    [Fact]
    public async Task SetEnabledFromOwnerAsync_WhenEnabling_UpdatesStore()
    {
        var store = new StubModuleFlagStore();
        var now = new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);
        var workflow = new CompanyModuleFlagWorkflow(store, () => now);

        var result = await workflow.SetEnabledFromOwnerAsync(
            CompanyId,
            CompanyModuleFlagCatalog.Task,
            enabled: true,
            "Owner enabled Task.",
            ActorUserId,
            CancellationToken.None);

        Assert.True(result.Flag.Enabled);
        Assert.Equal(now.AddDays(CompanyModuleFlagCatalog.DefaultSelfServiceAccessDays), result.Flag.AccessExpiresAtUtc);
        Assert.True(result.Changed);
        Assert.Equal(1, store.SetEnabledCallCount);
        Assert.Equal("user", store.LastActorType);
        Assert.Equal(ActorUserId, store.LastActorUserId);
    }

    [Fact]
    public async Task SetEnabledFromOwnerAsync_WhenDisabling_RejectsWithoutUpdatingStore()
    {
        var store = new StubModuleFlagStore();
        var workflow = new CompanyModuleFlagWorkflow(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SetEnabledFromOwnerAsync(
                CompanyId,
                CompanyModuleFlagCatalog.Task,
                enabled: false,
                "Owner disabled Task.",
                ActorUserId,
                CancellationToken.None));

        Assert.Contains("cannot be disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SetEnabledCallCount);
    }

    [Fact]
    public async Task GetAccessStatusAsync_WhenExpired_AllowsReadButNotWrite()
    {
        var now = new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);
        var store = new StubModuleFlagStore
        {
            SeededStatus = new CompanyModuleFlagAccessStatus(
                CompanyId,
                CompanyModuleFlagCatalog.Task,
                Enabled: true,
                AccessExpiresAtUtc: now.AddMinutes(-1),
                IsExpired: true)
        };
        var workflow = new CompanyModuleFlagWorkflow(store, () => now);

        var status = await workflow.GetAccessStatusAsync(
            CompanyId,
            CompanyModuleFlagCatalog.Task,
            CancellationToken.None);

        Assert.True(status.AllowsRead);
        Assert.False(status.AllowsWrite);
        Assert.True(status.IsExpired);
    }

    private sealed class StubModuleFlagStore : ICompanyModuleFlagStore
    {
        public int SetEnabledCallCount { get; private set; }
        public string? LastActorType { get; private set; }
        public UserId? LastActorUserId { get; private set; }
        public CompanyModuleFlagAccessStatus? SeededStatus { get; init; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CompanyModuleFlagSummary>> ListAsync(
            CompanyId companyId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompanyModuleFlagSummary>>([]);

        public Task<bool> IsEnabledAsync(
            CompanyId companyId,
            string moduleKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<CompanyModuleFlagAccessStatus> GetAccessStatusAsync(
            CompanyId companyId,
            string moduleKey,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(SeededStatus ?? new CompanyModuleFlagAccessStatus(
                companyId,
                moduleKey,
                Enabled: false,
                AccessExpiresAtUtc: null,
                IsExpired: false));

        public Task<CompanyModuleFlagUpdateResult> SetEnabledAsync(
            CompanyId companyId,
            string moduleKey,
            bool enabled,
            string reason,
            string actorType,
            UserId? actorUserId,
            DateTimeOffset? accessExpiresAtUtc,
            bool forceAuditOnNoChange,
            CancellationToken cancellationToken)
        {
            SetEnabledCallCount++;
            LastActorType = actorType;
            LastActorUserId = actorUserId;

            return Task.FromResult(new CompanyModuleFlagUpdateResult(
                new CompanyModuleFlagSummary(
                    companyId,
                    moduleKey,
                    "Task",
                    "Service-delivery execution units.",
                    enabled,
                    accessExpiresAtUtc,
                    enabled && accessExpiresAtUtc.HasValue && accessExpiresAtUtc.Value <= DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    actorUserId),
                Changed: true,
                reason));
        }
    }
}
