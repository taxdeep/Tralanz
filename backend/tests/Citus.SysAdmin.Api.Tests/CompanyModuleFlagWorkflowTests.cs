using Modules.Company.FeatureManagement;
using SharedKernel.Identity;

namespace Citus.SysAdmin.Api.Tests;

public class CompanyModuleFlagWorkflowTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);
    private static readonly CompanyId CompanyB = CompanyId.FromOrdinal(2);

    [Fact]
    public void GetAvailableModules_returns_catalog()
    {
        var workflow = new CompanyModuleFlagWorkflow(new FakeStore());
        var modules = workflow.GetAvailableModules();

        Assert.Contains(modules, m => m.Key == "task");
    }

    [Fact]
    public async Task IsEnabled_returns_false_for_unknown_key_without_touching_store()
    {
        var store = new FakeStore();
        var workflow = new CompanyModuleFlagWorkflow(store);

        var enabled = await workflow.IsEnabledAsync(CompanyA, "payroll", CancellationToken.None);

        Assert.False(enabled);
        Assert.Equal(0, store.IsEnabledCalls);
    }

    [Fact]
    public async Task IsEnabled_caches_value_inside_ttl()
    {
        var store = new FakeStore(initiallyEnabled: true);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var workflow = new CompanyModuleFlagWorkflow(store, () => clock.Now);

        var first = await workflow.IsEnabledAsync(CompanyA, "task", CancellationToken.None);
        var second = await workflow.IsEnabledAsync(CompanyA, "task", CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, store.IsEnabledCalls);
    }

    [Fact]
    public async Task IsEnabled_refetches_after_ttl_expires()
    {
        var store = new FakeStore(initiallyEnabled: false);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var workflow = new CompanyModuleFlagWorkflow(store, () => clock.Now);

        _ = await workflow.IsEnabledAsync(CompanyA, "task", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = await workflow.IsEnabledAsync(CompanyA, "task", CancellationToken.None);

        Assert.Equal(2, store.IsEnabledCalls);
    }

    [Fact]
    public async Task IsEnabled_isolates_by_company()
    {
        var store = new FakeStore();
        store.SetState(CompanyA, "task", true);
        store.SetState(CompanyB, "task", false);
        var workflow = new CompanyModuleFlagWorkflow(store);

        var a = await workflow.IsEnabledAsync(CompanyA, "task", CancellationToken.None);
        var b = await workflow.IsEnabledAsync(CompanyB, "task", CancellationToken.None);

        Assert.True(a);
        Assert.False(b);
    }

    [Fact]
    public async Task SetEnabled_invalidates_cache_so_next_read_sees_new_value()
    {
        var store = new FakeStore(initiallyEnabled: false);
        var workflow = new CompanyModuleFlagWorkflow(store);

        var before = await workflow.IsEnabledAsync(CompanyA, "task", CancellationToken.None);
        await workflow.SetEnabledFromSysAdminAsync(
            CompanyA,
            "task",
            enabled: true,
            reason: "test",
            sysAdminAccountId: null,
            CancellationToken.None);
        var after = await workflow.IsEnabledAsync(CompanyA, "task", CancellationToken.None);

        Assert.False(before);
        Assert.True(after);
        // After SetEnabled the cache is primed with the new value, so
        // the post-write IsEnabled is served from cache, not the store.
        Assert.Equal(1, store.IsEnabledCalls);
        Assert.Equal(1, store.SetEnabledCalls);
    }

    [Fact]
    public async Task SetEnabled_rejects_unknown_module_key()
    {
        var workflow = new CompanyModuleFlagWorkflow(new FakeStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SetEnabledFromSysAdminAsync(
                CompanyA,
                "payroll",
                enabled: true,
                reason: "test",
                sysAdminAccountId: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task SetEnabled_normalizes_module_key_before_persisting()
    {
        var store = new FakeStore();
        var workflow = new CompanyModuleFlagWorkflow(store);

        await workflow.SetEnabledFromSysAdminAsync(
            CompanyA,
            "  TASK  ",
            enabled: true,
            reason: "test",
            sysAdminAccountId: null,
            CancellationToken.None);

        Assert.Equal("task", store.LastSetModuleKey);
    }

    [Fact]
    public async Task SetEnabled_uses_default_reason_when_caller_omits_one()
    {
        var store = new FakeStore();
        var workflow = new CompanyModuleFlagWorkflow(store);

        await workflow.SetEnabledFromSysAdminAsync(
            CompanyA,
            "task",
            enabled: true,
            reason: "   ",
            sysAdminAccountId: null,
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(store.LastSetReason));
        Assert.Contains("task", store.LastSetReason!);
    }

    [Fact]
    public async Task SetEnabled_records_actor_type_sysadmin()
    {
        var store = new FakeStore();
        var workflow = new CompanyModuleFlagWorkflow(store);

        await workflow.SetEnabledFromSysAdminAsync(
            CompanyA,
            "task",
            enabled: true,
            reason: "test",
            sysAdminAccountId: null,
            CancellationToken.None);

        Assert.Equal("sysadmin", store.LastSetActorType);
    }

    private sealed class TestClock
    {
        public TestClock(DateTimeOffset initial) => Now = initial;

        public DateTimeOffset Now { get; private set; }

        public void Advance(TimeSpan delta) => Now += delta;
    }

    private sealed class FakeStore : ICompanyModuleFlagStore
    {
        private readonly Dictionary<(CompanyId, string), bool> _state = new();

        public FakeStore(bool initiallyEnabled = false)
        {
            if (initiallyEnabled)
            {
                _state[(CompanyA, "task")] = true;
            }
        }

        public int IsEnabledCalls { get; private set; }

        public int SetEnabledCalls { get; private set; }

        public string? LastSetModuleKey { get; private set; }

        public string? LastSetReason { get; private set; }

        public string? LastSetActorType { get; private set; }

        public void SetState(CompanyId companyId, string moduleKey, bool enabled) =>
            _state[(companyId, moduleKey)] = enabled;

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<CompanyModuleFlagSummary>> ListAsync(
            CompanyId companyId,
            CancellationToken cancellationToken)
        {
            var summaries = CompanyModuleFlagCatalog.Options.Select(option =>
                _state.TryGetValue((companyId, option.Key), out var enabled)
                    ? new CompanyModuleFlagSummary(
                        companyId,
                        option.Key,
                        option.DisplayName,
                        option.Description,
                        enabled,
                        DateTimeOffset.UtcNow,
                        UpdatedByUserId: null)
                    : new CompanyModuleFlagSummary(
                        companyId,
                        option.Key,
                        option.DisplayName,
                        option.Description,
                        Enabled: false,
                        UpdatedAtUtc: null,
                        UpdatedByUserId: null)).ToArray();

            return Task.FromResult<IReadOnlyList<CompanyModuleFlagSummary>>(summaries);
        }

        public Task<bool> IsEnabledAsync(CompanyId companyId, string moduleKey, CancellationToken cancellationToken)
        {
            IsEnabledCalls++;
            return Task.FromResult(_state.TryGetValue((companyId, moduleKey), out var enabled) && enabled);
        }

        public Task<CompanyModuleFlagUpdateResult> SetEnabledAsync(
            CompanyId companyId,
            string moduleKey,
            bool enabled,
            string reason,
            string actorType,
            UserId? actorUserId,
            bool forceAuditOnNoChange,
            CancellationToken cancellationToken)
        {
            SetEnabledCalls++;
            LastSetModuleKey = moduleKey;
            LastSetReason = reason;
            LastSetActorType = actorType;
            _state[(companyId, moduleKey)] = enabled;
            var option = CompanyModuleFlagCatalog.Options.First(o => o.Key == moduleKey);
            var summary = new CompanyModuleFlagSummary(
                companyId,
                moduleKey,
                option.DisplayName,
                option.Description,
                enabled,
                DateTimeOffset.UtcNow,
                actorUserId);
            return Task.FromResult(new CompanyModuleFlagUpdateResult(summary, Changed: true, reason));
        }
    }
}
