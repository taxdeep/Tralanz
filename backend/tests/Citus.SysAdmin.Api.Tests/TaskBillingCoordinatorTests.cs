using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Citus.Modules.Tasks.Application;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;
using SharedKernel.Identity;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.SysAdmin.Api.Tests;

public class TaskBillingCoordinatorTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);
    private static readonly CompanyId CompanyB = CompanyId.FromOrdinal(2);
    private static readonly UserId Actor = UserId.Parse("U000001");
    private static readonly Guid CustomerX = Guid.NewGuid();
    private static readonly Guid CustomerY = Guid.NewGuid();

    [Fact]
    public async Task MarkAsBilled_transitions_each_completed_task_and_records_invoice()
    {
        var store = new InMemoryTaskStore();
        var t1 = store.Seed(TaskStatus.Completed, customerId: CustomerX);
        var t2 = store.Seed(TaskStatus.Completed, customerId: CustomerX);
        var coordinator = BuildCoordinator(store);
        var invoiceId = Guid.NewGuid();

        var result = await coordinator.MarkAsBilledAsync(
            CompanyA, invoiceId, CustomerX, new[] { t1, t2 }, Actor, CancellationToken.None);

        Assert.Equal(invoiceId, result.InvoiceId);
        Assert.Equal(2, result.ProcessedTasks.Count);
        Assert.Empty(result.SkippedTasks);
        Assert.Equal(TaskStatus.Billed, store.GetStatus(t1));
        Assert.Equal(TaskStatus.Billed, store.GetStatus(t2));
        Assert.Equal(invoiceId, store.GetBilledInvoice(t1));
        Assert.Equal(invoiceId, store.GetBilledInvoice(t2));
    }

    [Fact]
    public async Task MarkAsBilled_is_idempotent_when_replayed_with_same_invoice()
    {
        var store = new InMemoryTaskStore();
        var taskId = store.Seed(TaskStatus.Completed, customerId: CustomerX);
        var coordinator = BuildCoordinator(store);
        var invoiceId = Guid.NewGuid();

        var first = await coordinator.MarkAsBilledAsync(
            CompanyA, invoiceId, CustomerX, new[] { taskId }, Actor, CancellationToken.None);
        Assert.Single(first.ProcessedTasks);
        Assert.Empty(first.SkippedTasks);

        // Second call with the same (invoice, task) pair must not throw
        // and must not transition the row again — the AR void path
        // depends on this for safe retries.
        var second = await coordinator.MarkAsBilledAsync(
            CompanyA, invoiceId, CustomerX, new[] { taskId }, Actor, CancellationToken.None);
        Assert.Empty(second.ProcessedTasks);
        Assert.Single(second.SkippedTasks);
    }

    [Fact]
    public async Task MarkAsBilled_throws_when_task_is_billed_by_a_different_invoice()
    {
        var store = new InMemoryTaskStore();
        var taskId = store.Seed(TaskStatus.Completed, customerId: CustomerX);
        var coordinator = BuildCoordinator(store);
        var originalInvoice = Guid.NewGuid();
        await coordinator.MarkAsBilledAsync(
            CompanyA, originalInvoice, CustomerX, new[] { taskId }, Actor, CancellationToken.None);

        var differentInvoice = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkAsBilledAsync(
                CompanyA, differentInvoice, CustomerX, new[] { taskId }, Actor, CancellationToken.None));
        Assert.Contains("already billed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalInvoice, store.GetBilledInvoice(taskId));
    }

    [Fact]
    public async Task MarkAsBilled_rejects_task_in_non_completed_status()
    {
        var store = new InMemoryTaskStore();
        var openId = store.Seed(TaskStatus.Open, customerId: CustomerX);
        var coordinator = BuildCoordinator(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkAsBilledAsync(
                CompanyA, Guid.NewGuid(), CustomerX, new[] { openId }, Actor, CancellationToken.None));

        Assert.Equal(TaskStatus.Open, store.GetStatus(openId));
    }

    [Fact]
    public async Task MarkAsBilled_rejects_when_customer_does_not_match_task_customer()
    {
        var store = new InMemoryTaskStore();
        var taskId = store.Seed(TaskStatus.Completed, customerId: CustomerX);
        var coordinator = BuildCoordinator(store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkAsBilledAsync(
                CompanyA, Guid.NewGuid(), CustomerY, new[] { taskId }, Actor, CancellationToken.None));
        Assert.Contains("different customer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkAsBilled_allows_null_customer_filter_when_caller_skips_match()
    {
        var store = new InMemoryTaskStore();
        var taskId = store.Seed(TaskStatus.Completed, customerId: CustomerX);
        var coordinator = BuildCoordinator(store);

        var result = await coordinator.MarkAsBilledAsync(
            CompanyA, Guid.NewGuid(), customerId: null, new[] { taskId }, Actor, CancellationToken.None);

        Assert.Single(result.ProcessedTasks);
    }

    [Fact]
    public async Task MarkAsBilled_throws_when_a_task_does_not_belong_to_the_active_company()
    {
        var store = new InMemoryTaskStore();
        var foreignId = store.Seed(TaskStatus.Completed, customerId: CustomerX, company: CompanyB);
        var coordinator = BuildCoordinator(store);

        // Same Guid in CompanyA returns null from the store; the
        // coordinator phrases this as a cross-company protection error.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkAsBilledAsync(
                CompanyA, Guid.NewGuid(), CustomerX, new[] { foreignId }, Actor, CancellationToken.None));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkAsBilled_rejects_empty_invoice_id()
    {
        var coordinator = BuildCoordinator(new InMemoryTaskStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkAsBilledAsync(
                CompanyA, Guid.Empty, CustomerX, new[] { Guid.NewGuid() }, Actor, CancellationToken.None));
    }

    [Fact]
    public async Task MarkAsBilled_rejects_empty_task_list()
    {
        var coordinator = BuildCoordinator(new InMemoryTaskStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkAsBilledAsync(
                CompanyA, Guid.NewGuid(), CustomerX, Array.Empty<Guid>(), Actor, CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_restores_every_task_billed_by_the_invoice_to_completed()
    {
        var store = new InMemoryTaskStore();
        var t1 = store.Seed(TaskStatus.Completed, customerId: CustomerX);
        var t2 = store.Seed(TaskStatus.Completed, customerId: CustomerX);
        var coordinator = BuildCoordinator(store);
        var invoiceId = Guid.NewGuid();
        await coordinator.MarkAsBilledAsync(
            CompanyA, invoiceId, CustomerX, new[] { t1, t2 }, Actor, CancellationToken.None);

        var result = await coordinator.RollbackBillingAsync(
            CompanyA, invoiceId, Actor, reason: "void", CancellationToken.None);

        Assert.Equal(invoiceId, result.InvoiceId);
        Assert.Equal(2, result.ProcessedTasks.Count);
        Assert.Empty(result.SkippedTasks);
        Assert.Equal(TaskStatus.Completed, store.GetStatus(t1));
        Assert.Equal(TaskStatus.Completed, store.GetStatus(t2));
    }

    [Fact]
    public async Task Rollback_is_noop_when_no_tasks_were_billed_by_invoice()
    {
        var coordinator = BuildCoordinator(new InMemoryTaskStore());

        var result = await coordinator.RollbackBillingAsync(
            CompanyA, Guid.NewGuid(), Actor, reason: null, CancellationToken.None);

        Assert.Empty(result.ProcessedTasks);
        Assert.Empty(result.SkippedTasks);
    }

    [Fact]
    public async Task Rollback_can_be_replayed_safely_after_partial_recovery()
    {
        var store = new InMemoryTaskStore();
        var taskId = store.Seed(TaskStatus.Completed, customerId: CustomerX);
        var coordinator = BuildCoordinator(store);
        var invoiceId = Guid.NewGuid();
        await coordinator.MarkAsBilledAsync(
            CompanyA, invoiceId, CustomerX, new[] { taskId }, Actor, CancellationToken.None);

        var first = await coordinator.RollbackBillingAsync(CompanyA, invoiceId, Actor, null, CancellationToken.None);
        Assert.Single(first.ProcessedTasks);

        // Second rollback finds no rows still in Billed; the lookup
        // returns the (now restored) row but the per-row guard reports
        // it as skipped without throwing.
        var second = await coordinator.RollbackBillingAsync(CompanyA, invoiceId, Actor, null, CancellationToken.None);
        Assert.Empty(second.ProcessedTasks);
    }

    private static ITaskBillingCoordinator BuildCoordinator(InMemoryTaskStore store)
    {
        var workflow = new TaskWorkflow(store, NullResolver.Instance, NullProjectionStore.Instance);
        return new TaskBillingCoordinator(store, workflow);
    }

    private sealed class NullResolver : IItemPriceResolver
    {
        public static readonly NullResolver Instance = new();
        public Task<InventoryItemPriceResolution?> ResolveAsync(InventoryItemPriceQuery query, CancellationToken ct) =>
            Task.FromResult<InventoryItemPriceResolution?>(null);
    }

    private sealed class NullProjectionStore : IUnitySearchProjectionStore
    {
        public static readonly NullProjectionStore Instance = new();
        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EnsureProjectionFreshAsync(CompanyId companyId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InvalidateAsync(CompanyId companyId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>> GetDisplayNamesAsync(
            CompanyId companyId,
            IReadOnlyCollection<(string EntityType, Guid SourceId)> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>>(
                new Dictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>());
    }

    // Minimal multi-row store that supports the coordinator's three
    // call surfaces: GetAsync, TransitionStatusAsync, and the new
    // ListByBilledInvoiceAsync. Keys are (company, taskId) so we can
    // model cross-company rows in one store.
    private sealed class InMemoryTaskStore : ITaskStore
    {
        private readonly Dictionary<(CompanyId Company, Guid Id), TaskRecord> _rows = new();

        public Guid Seed(TaskStatus status, Guid? customerId = null, CompanyId? company = null)
        {
            var id = Guid.NewGuid();
            var owner = company ?? CompanyA;
            _rows[(owner, id)] = new TaskRecord
            {
                Id = id,
                CompanyId = owner,
                TaskNo = $"TSK-{_rows.Count + 1:D6}",
                Title = "seed",
                CustomerId = customerId,
                Status = status,
                CurrencyCode = "USD",
                TotalBillableValue = 100m,
                IsVoided = false,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = Actor,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Lines = Array.Empty<TaskLineRecord>(),
            };
            return id;
        }

        public TaskStatus GetStatus(Guid id) =>
            _rows.First(kv => kv.Key.Id == id).Value.Status;

        public Guid? GetBilledInvoice(Guid id) =>
            _rows.First(kv => kv.Key.Id == id).Value.BilledInvoiceId;

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TaskRecord?> GetAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.TryGetValue((companyId, taskId), out var row) ? row : null);

        public Task<IReadOnlyList<TaskSummary>> ListAsync(TaskQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSummary>>(Array.Empty<TaskSummary>());

        public Task<TaskRecord> CreateAsync(
            CompanyId companyId, UserId createdBy, string title, string? description,
            Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate,
            string currencyCode, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TaskRecord?> UpdateHeaderAsync(
            CompanyId companyId, Guid taskId, string title, string? description,
            Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TaskRecord> AppendLineAsync(
            CompanyId companyId, Guid taskId, Guid itemId, string? description,
            decimal quantity, decimal unitPrice, string currencyCode, Guid? taxCodeId,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TaskRecord> RemoveLineAsync(CompanyId companyId, Guid taskId, Guid lineId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TaskRecord?> TransitionStatusAsync(
            CompanyId companyId, Guid taskId, TaskStatus fromStatus, TaskStatus toStatus,
            UserId actorUserId, string? reason, Guid? billedInvoiceId, CancellationToken cancellationToken)
        {
            if (!_rows.TryGetValue((companyId, taskId), out var row)) return Task.FromResult<TaskRecord?>(null);
            if (row.Status != fromStatus)
            {
                throw new InvalidOperationException(
                    $"Concurrent transition: expected '{fromStatus}', found '{row.Status}'.");
            }
            var updated = row with
            {
                Status = toStatus,
                BilledInvoiceId = toStatus == TaskStatus.Billed
                    ? billedInvoiceId
                    : (toStatus == TaskStatus.Completed && row.Status == TaskStatus.Billed ? null : row.BilledInvoiceId),
                BilledAtUtc = toStatus == TaskStatus.Billed ? DateTimeOffset.UtcNow : row.BilledAtUtc,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _rows[(companyId, taskId)] = updated;
            return Task.FromResult<TaskRecord?>(updated);
        }

        public Task<IReadOnlyList<TaskStateTransitionRecord>> ListTransitionsAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskStateTransitionRecord>>(Array.Empty<TaskStateTransitionRecord>());

        public Task<IReadOnlyList<TaskSummary>> ListByBilledInvoiceAsync(CompanyId companyId, Guid invoiceId, CancellationToken cancellationToken)
        {
            var hits = _rows
                .Where(kv => kv.Key.Company == companyId && kv.Value.BilledInvoiceId == invoiceId)
                .Select(kv => kv.Value)
                .Select(r => new TaskSummary
                {
                    Id = r.Id,
                    CompanyId = r.CompanyId,
                    TaskNo = r.TaskNo,
                    Title = r.Title,
                    CustomerId = r.CustomerId,
                    AssignedToUserId = r.AssignedToUserId,
                    Status = r.Status,
                    ServiceDate = r.ServiceDate,
                    TotalBillableValue = r.TotalBillableValue,
                    CurrencyCode = r.CurrencyCode,
                    UpdatedAtUtc = r.UpdatedAtUtc,
                })
                .ToArray();
            return Task.FromResult<IReadOnlyList<TaskSummary>>(hits);
        }

        public Task<IReadOnlyList<TaskDisplayLookup>> LookupDisplayAsync(CompanyId companyId, IReadOnlyList<Guid> taskIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskDisplayLookup>>(Array.Empty<TaskDisplayLookup>());

        // H6-2: not exercised by these legacy whole-task tests — the
        // new MarkLinesAsBilled path is covered in a dedicated test
        // class with its own line-aware store stub.
        public Task<TaskLineBillingStampOutcome> MarkLineBilledAsync(
            CompanyId companyId, Guid taskLineId, string sourceType, Guid sourceId,
            Guid? sourceLineId, DateTimeOffset billedAtUtc, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TaskLineBillingSnapshot?> ReadLineBillingSnapshotAsync(
            CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
