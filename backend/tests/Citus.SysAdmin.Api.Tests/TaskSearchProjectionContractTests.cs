using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Citus.Modules.Tasks.Application;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.UnitySearch.Application;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;
using Modules.CompanyAccess.Memberships;
using SharedKernel.Identity;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.SysAdmin.Api.Tests;

/// <summary>
/// Batch 7 wiring: the static contract between Tasks, the search
/// projection seeder, and the policy registry. The Postgres seeder
/// SQL itself is exercised by integration tests against a real DB;
/// these tests pin the C# invariants (token names, policy contents,
/// invalidation hooks) that the SQL relies on.
/// </summary>
public class TaskSearchProjectionContractTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);
    private static readonly UserId Actor = UserId.Parse("U000001");

    [Fact]
    public void SearchDocumentType_Task_constant_is_lowercase_task()
    {
        // The seeder hard-codes 'task' as the entity_type literal in
        // its INSERT SELECT. If the constant ever drifts, the policy
        // registry would silently stop returning Task rows.
        Assert.Equal("task", SearchDocumentType.Task);
    }

    [Fact]
    public void GlobalTopbar_policy_includes_task()
    {
        var registry = new UnitySearchPolicyRegistry();
        var policy = registry.Resolve(SearchScopeContext.GlobalTopbar);
        Assert.Contains(SearchDocumentType.Task, policy.EntityTypes);
    }

    [Fact]
    public void GlobalTransactions_policy_includes_task()
    {
        var registry = new UnitySearchPolicyRegistry();
        var policy = registry.Resolve(SearchScopeContext.GlobalTransactions);
        Assert.Contains(SearchDocumentType.Task, policy.EntityTypes);
    }

    [Fact]
    public void TaskPicker_policy_returns_task_only()
    {
        var registry = new UnitySearchPolicyRegistry();
        var policy = registry.Resolve(SearchScopeContext.TaskPicker);
        Assert.Equal(new[] { SearchDocumentType.Task }, policy.EntityTypes);
        Assert.True(policy.EnforceActiveOnly);
        Assert.True(policy.EnforceBusinessEligibility);
    }

    [Fact]
    public void TaskView_and_TaskViewAll_tokens_exist_in_permission_catalog()
    {
        // The seeder writes required_permissions=['task.view'] and
        // visibility_override_permission='task.view.all'. Both tokens
        // must be present in the catalog or the migration that drops
        // unknown tokens would silently neutralise the projection.
        Assert.Contains("task.view", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Contains("task.view.all", CompanyMembershipPermissionCatalog.AllTokens);
    }

    [Fact]
    public void SearchDocumentRecord_carries_visibility_override_permission()
    {
        var record = new SearchDocumentRecord(
            CompanyA,
            EntityType: SearchDocumentType.Task,
            SourceId: Guid.NewGuid(),
            GroupKey: SearchGroupKey.Transactions,
            PrimaryText: "TSK-1",
            SecondaryText: "Open",
            SearchText: "TSK-1",
            ExactCodeNorm: "tsk-1",
            NavigationHref: "/tasks/abc",
            MetadataJson: "{}",
            EffectiveDate: null,
            Amount: null,
            IsActive: true,
            IsVoided: false,
            RankBoost: 40m,
            Version: 1L,
            ComputedScore: 0m,
            ModuleKey: "task",
            RequiredPermissions: new[] { "task.view" },
            OwnerUserId: Actor,
            VisibilityScope: "assignee_only",
            VisibilityOverridePermission: "task.view.all");

        Assert.Equal("task.view.all", record.VisibilityOverridePermission);
    }

    [Fact]
    public async Task TaskWorkflow_invalidates_search_after_create()
    {
        var projection = new RecordingProjectionStore();
        var workflow = new TaskWorkflow(new MutationCapturingStore(), new StubResolver(10m), projection);

        await workflow.CreateAsync(
            CompanyA,
            Actor,
            new TaskCreateRequest { Title = "X", CurrencyCode = "USD" },
            CancellationToken.None);

        Assert.Contains(CompanyA, projection.Invalidated);
    }

    [Fact]
    public async Task TaskWorkflow_invalidates_search_after_state_transition()
    {
        var projection = new RecordingProjectionStore();
        var store = new MutationCapturingStore();
        store.SeedCompletedTask();
        var workflow = new TaskWorkflow(store, new StubResolver(10m), projection);

        await workflow.MarkBilledAsync(CompanyA, store.SeededId, Guid.NewGuid(), Actor, CancellationToken.None);

        Assert.Contains(CompanyA, projection.Invalidated);
    }

    private sealed class RecordingProjectionStore : IUnitySearchProjectionStore
    {
        public List<CompanyId> Invalidated { get; } = new();

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EnsureProjectionFreshAsync(CompanyId companyId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InvalidateAsync(CompanyId companyId, CancellationToken cancellationToken)
        {
            Invalidated.Add(companyId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>> GetDisplayNamesAsync(
            CompanyId companyId,
            IReadOnlyCollection<(string EntityType, Guid SourceId)> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>>(
                new Dictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>());
    }

    private sealed class StubResolver(decimal unitPrice) : IItemPriceResolver
    {
        public Task<InventoryItemPriceResolution?> ResolveAsync(InventoryItemPriceQuery query, CancellationToken ct) =>
            Task.FromResult<InventoryItemPriceResolution?>(new InventoryItemPriceResolution
            {
                PriceId = Guid.NewGuid(),
                CompanyId = query.CompanyId,
                ItemId = query.ItemId,
                CurrencyCode = query.CurrencyCode,
                UnitPrice = unitPrice,
                MinQuantity = 1m,
                EffectiveFrom = query.AsOf,
                EffectiveTo = null,
                PriceListCode = null,
                CustomerId = null,
                MatchedScope = InventoryItemPriceScope.Generic,
            });
    }

    private sealed class MutationCapturingStore : ITaskStore
    {
        private TaskRecord? _seeded;

        public Guid SeededId => _seeded?.Id ?? Guid.Empty;

        public void SeedCompletedTask()
        {
            var id = Guid.NewGuid();
            _seeded = Build(id, TaskStatus.Completed);
        }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TaskRecord?> GetAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken)
        {
            if (_seeded?.Id == taskId) return Task.FromResult<TaskRecord?>(_seeded);
            return Task.FromResult<TaskRecord?>(null);
        }

        public Task<IReadOnlyList<TaskSummary>> ListAsync(TaskQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSummary>>(Array.Empty<TaskSummary>());

        public Task<TaskRecord> CreateAsync(
            CompanyId companyId, UserId createdBy, string title, string? description,
            Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate,
            string currencyCode, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            _seeded = Build(id, TaskStatus.Open, currency: currencyCode);
            return Task.FromResult(_seeded);
        }

        public Task<TaskRecord?> UpdateHeaderAsync(
            CompanyId companyId, Guid taskId, string title, string? description,
            Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate,
            CancellationToken cancellationToken) => Task.FromResult<TaskRecord?>(_seeded);

        public Task<TaskRecord> AppendLineAsync(
            CompanyId companyId, Guid taskId, Guid itemId, string? description,
            decimal quantity, decimal unitPrice, string currencyCode, Guid? taxCodeId,
            CancellationToken cancellationToken) => Task.FromResult(_seeded!);

        public Task<TaskRecord> RemoveLineAsync(CompanyId companyId, Guid taskId, Guid lineId, CancellationToken cancellationToken) =>
            Task.FromResult(_seeded!);

        public Task<TaskRecord?> TransitionStatusAsync(
            CompanyId companyId, Guid taskId, TaskStatus fromStatus, TaskStatus toStatus,
            UserId actorUserId, string? reason, Guid? billedInvoiceId, CancellationToken cancellationToken)
        {
            if (_seeded is null) return Task.FromResult<TaskRecord?>(null);
            _seeded = _seeded with { Status = toStatus };
            return Task.FromResult<TaskRecord?>(_seeded);
        }

        public Task<IReadOnlyList<TaskStateTransitionRecord>> ListTransitionsAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskStateTransitionRecord>>(Array.Empty<TaskStateTransitionRecord>());

        public Task<IReadOnlyList<TaskSummary>> ListByBilledInvoiceAsync(CompanyId companyId, Guid invoiceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSummary>>(Array.Empty<TaskSummary>());

        public Task<IReadOnlyList<TaskDisplayLookup>> LookupDisplayAsync(CompanyId companyId, IReadOnlyList<Guid> taskIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskDisplayLookup>>(Array.Empty<TaskDisplayLookup>());

        private static TaskRecord Build(Guid id, TaskStatus status, string currency = "USD") =>
            new()
            {
                Id = id,
                CompanyId = CompanyA,
                TaskNo = "TSK-000001",
                Title = "seed",
                Status = status,
                CurrencyCode = currency,
                TotalBillableValue = 0m,
                IsVoided = false,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = Actor,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Lines = Array.Empty<TaskLineRecord>(),
            };
    }
}
