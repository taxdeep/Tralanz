using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Citus.Modules.Tasks.Application;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;
using Modules.CompanyAccess.Memberships;
using SharedKernel.Identity;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.SysAdmin.Api.Tests;

public class TaskWorkflowStateMachineTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);
    private static readonly UserId Actor = UserId.Parse("U000001");

    [Fact]
    public async Task Create_starts_task_in_open_status_with_normalized_currency()
    {
        var store = new RecordingTaskStore();
        var workflow = new TaskWorkflow(store, NullResolver.Instance, NullProjectionStore.Instance);

        var created = await workflow.CreateAsync(
            CompanyA,
            Actor,
            new TaskCreateRequest
            {
                Title = "  Service A  ",
                CurrencyCode = "usd",
            },
            CancellationToken.None);

        Assert.Equal(TaskStatus.Open, created.Status);
        Assert.Equal("Service A", created.Title);
        Assert.Equal("USD", created.CurrencyCode);
        Assert.Equal(Actor, created.CreatedBy);
    }

    [Fact]
    public async Task Create_rejects_blank_title()
    {
        var workflow = new TaskWorkflow(new RecordingTaskStore(), NullResolver.Instance, NullProjectionStore.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.CreateAsync(
                CompanyA, Actor,
                new TaskCreateRequest { Title = "   ", CurrencyCode = "USD" },
                CancellationToken.None));
    }

    [Fact]
    public async Task Create_rejects_non_iso_currency()
    {
        var workflow = new TaskWorkflow(new RecordingTaskStore(), NullResolver.Instance, NullProjectionStore.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.CreateAsync(
                CompanyA, Actor,
                new TaskCreateRequest { Title = "X", CurrencyCode = "DOLLAR" },
                CancellationToken.None));
    }

    [Fact]
    public async Task AddLine_falls_back_to_price_resolver_when_unit_price_is_omitted()
    {
        var store = new RecordingTaskStore();
        store.SeedOpenTask(currency: "USD");
        var resolver = new StubResolver(unitPrice: 42m);
        var workflow = new TaskWorkflow(store, resolver, NullProjectionStore.Instance);

        await workflow.AddLineAsync(
            CompanyA,
            store.SeededTaskId,
            Actor,
            new TaskLineUpsertRequest
            {
                ItemId = Guid.NewGuid(),
                Quantity = 2m,
                UnitPrice = null,
            },
            CancellationToken.None);

        Assert.Equal(42m, store.LastAppendedUnitPrice);
        Assert.NotNull(resolver.LastQuery);
        Assert.Equal("USD", resolver.LastQuery!.CurrencyCode);
    }

    [Fact]
    public async Task AddLine_uses_explicit_unit_price_and_skips_resolver()
    {
        var store = new RecordingTaskStore();
        store.SeedOpenTask(currency: "USD");
        var resolver = new StubResolver(unitPrice: 999m);
        var workflow = new TaskWorkflow(store, resolver, NullProjectionStore.Instance);

        await workflow.AddLineAsync(
            CompanyA,
            store.SeededTaskId,
            Actor,
            new TaskLineUpsertRequest
            {
                ItemId = Guid.NewGuid(),
                Quantity = 1m,
                UnitPrice = 50m,
            },
            CancellationToken.None);

        Assert.Equal(50m, store.LastAppendedUnitPrice);
        Assert.Null(resolver.LastQuery);
    }

    [Fact]
    public async Task AddLine_throws_when_resolver_finds_nothing_and_caller_omits_price()
    {
        var store = new RecordingTaskStore();
        store.SeedOpenTask(currency: "USD");
        var workflow = new TaskWorkflow(store, NullResolver.Instance, NullProjectionStore.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.AddLineAsync(
                CompanyA,
                store.SeededTaskId,
                Actor,
                new TaskLineUpsertRequest { ItemId = Guid.NewGuid(), Quantity = 1m },
                CancellationToken.None));
    }

    [Fact]
    public async Task AddLine_rejects_non_positive_quantity()
    {
        var store = new RecordingTaskStore();
        store.SeedOpenTask(currency: "USD");
        var workflow = new TaskWorkflow(store, new StubResolver(10m), NullProjectionStore.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.AddLineAsync(
                CompanyA,
                store.SeededTaskId,
                Actor,
                new TaskLineUpsertRequest { ItemId = Guid.NewGuid(), Quantity = 0m },
                CancellationToken.None));
    }

    [Fact]
    public async Task Update_rejected_when_task_is_not_open()
    {
        var store = new RecordingTaskStore();
        store.SeedCompletedTask();
        var workflow = new TaskWorkflow(store, NullResolver.Instance, NullProjectionStore.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.UpdateAsync(
                CompanyA, store.SeededTaskId, Actor,
                new TaskUpdateRequest { Title = "renamed" },
                CancellationToken.None));
    }

    [Fact]
    public async Task Complete_rejects_open_task_without_lines()
    {
        var store = new RecordingTaskStore();
        store.SeedOpenTask(currency: "USD"); // no lines
        var workflow = new TaskWorkflow(store, NullResolver.Instance, NullProjectionStore.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.CompleteAsync(CompanyA, store.SeededTaskId, Actor, reason: null, CancellationToken.None));
    }

    [Fact]
    public async Task Complete_transitions_open_to_completed_when_lines_exist()
    {
        var store = new RecordingTaskStore();
        store.SeedOpenTaskWithLine(currency: "USD", unitPrice: 25m, quantity: 1m);
        var workflow = new TaskWorkflow(store, NullResolver.Instance, NullProjectionStore.Instance);

        var result = await workflow.CompleteAsync(CompanyA, store.SeededTaskId, Actor, reason: null, CancellationToken.None);

        Assert.Equal(TaskStatus.Completed, result.Status);
        Assert.Equal((TaskStatus.Open, TaskStatus.Completed), store.LastTransition);
    }

    [Fact]
    public async Task Cancel_allowed_from_open_and_completed_but_not_billed_or_canceled()
    {
        var openStore = new RecordingTaskStore();
        openStore.SeedOpenTaskWithLine(currency: "USD", unitPrice: 1m, quantity: 1m);
        var openWorkflow = new TaskWorkflow(openStore, NullResolver.Instance, NullProjectionStore.Instance);
        await openWorkflow.CancelAsync(CompanyA, openStore.SeededTaskId, Actor, "no longer needed", CancellationToken.None);
        Assert.Equal((TaskStatus.Open, TaskStatus.Canceled), openStore.LastTransition);

        var completedStore = new RecordingTaskStore();
        completedStore.SeedCompletedTask();
        var completedWorkflow = new TaskWorkflow(completedStore, NullResolver.Instance, NullProjectionStore.Instance);
        await completedWorkflow.CancelAsync(CompanyA, completedStore.SeededTaskId, Actor, null, CancellationToken.None);
        Assert.Equal((TaskStatus.Completed, TaskStatus.Canceled), completedStore.LastTransition);

        var billedStore = new RecordingTaskStore();
        billedStore.SeedTaskInStatus(TaskStatus.Billed);
        var billedWorkflow = new TaskWorkflow(billedStore, NullResolver.Instance, NullProjectionStore.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            billedWorkflow.CancelAsync(CompanyA, billedStore.SeededTaskId, Actor, null, CancellationToken.None));

        var canceledStore = new RecordingTaskStore();
        canceledStore.SeedTaskInStatus(TaskStatus.Canceled);
        var canceledWorkflow = new TaskWorkflow(canceledStore, NullResolver.Instance, NullProjectionStore.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            canceledWorkflow.CancelAsync(CompanyA, canceledStore.SeededTaskId, Actor, null, CancellationToken.None));
    }

    [Fact]
    public async Task MarkBilled_requires_completed_status_and_records_invoice_id()
    {
        var store = new RecordingTaskStore();
        store.SeedCompletedTask();
        var workflow = new TaskWorkflow(store, NullResolver.Instance, NullProjectionStore.Instance);

        var invoiceId = Guid.NewGuid();
        var result = await workflow.MarkBilledAsync(CompanyA, store.SeededTaskId, invoiceId, Actor, CancellationToken.None);

        Assert.Equal(TaskStatus.Billed, result.Status);
        Assert.Equal((TaskStatus.Completed, TaskStatus.Billed), store.LastTransition);
        Assert.Equal(invoiceId, store.LastBilledInvoiceId);
    }

    [Fact]
    public async Task MarkBilled_rejects_open_or_canceled_task()
    {
        var openStore = new RecordingTaskStore();
        openStore.SeedOpenTask(currency: "USD");
        var openWorkflow = new TaskWorkflow(openStore, NullResolver.Instance, NullProjectionStore.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            openWorkflow.MarkBilledAsync(CompanyA, openStore.SeededTaskId, Guid.NewGuid(), Actor, CancellationToken.None));

        var canceledStore = new RecordingTaskStore();
        canceledStore.SeedTaskInStatus(TaskStatus.Canceled);
        var canceledWorkflow = new TaskWorkflow(canceledStore, NullResolver.Instance, NullProjectionStore.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            canceledWorkflow.MarkBilledAsync(CompanyA, canceledStore.SeededTaskId, Guid.NewGuid(), Actor, CancellationToken.None));
    }

    [Fact]
    public async Task RestoreFromBilled_rolls_back_billed_to_completed()
    {
        var store = new RecordingTaskStore();
        store.SeedTaskInStatus(TaskStatus.Billed);
        var workflow = new TaskWorkflow(store, NullResolver.Instance, NullProjectionStore.Instance);

        var result = await workflow.RestoreFromBilledAsync(CompanyA, store.SeededTaskId, Actor, "invoice voided", CancellationToken.None);

        Assert.Equal(TaskStatus.Completed, result.Status);
        Assert.Equal((TaskStatus.Billed, TaskStatus.Completed), store.LastTransition);
    }

    [Fact]
    public void Catalog_includes_every_task_permission()
    {
        Assert.Contains("task.view", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Contains("task.view.all", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Contains("task.create", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Contains("task.edit", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Contains("task.complete", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Contains("task.cancel", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Contains("task.bill", CompanyMembershipPermissionCatalog.AllTokens);
    }

    private sealed class StubResolver(decimal unitPrice) : IItemPriceResolver
    {
        public InventoryItemPriceQuery? LastQuery { get; private set; }

        public Task<InventoryItemPriceResolution?> ResolveAsync(InventoryItemPriceQuery query, CancellationToken ct)
        {
            LastQuery = query;
            return Task.FromResult<InventoryItemPriceResolution?>(new InventoryItemPriceResolution
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

    private sealed class RecordingTaskStore : ITaskStore
    {
        private TaskRecord? _seeded;

        public Guid SeededTaskId => _seeded?.Id ?? Guid.Empty;

        public TaskStatus SeededStatus { get; set; } = TaskStatus.Open;

        public decimal? LastAppendedUnitPrice { get; private set; }

        public (TaskStatus From, TaskStatus To)? LastTransition { get; private set; }

        public Guid? LastBilledInvoiceId { get; private set; }

        public void SeedOpenTask(string currency) =>
            _seeded = BuildSeed(TaskStatus.Open, currency, lines: Array.Empty<TaskLineRecord>());

        public void SeedOpenTaskWithLine(string currency, decimal unitPrice, decimal quantity)
        {
            var taskId = Guid.NewGuid();
            var line = new TaskLineRecord(
                Id: Guid.NewGuid(),
                CompanyId: CompanyA,
                TaskId: taskId,
                LineNo: 1,
                ItemId: Guid.NewGuid(),
                Description: null,
                Quantity: quantity,
                UnitPrice: unitPrice,
                CurrencyCode: currency,
                LineAmount: quantity * unitPrice,
                TaxCodeId: null);
            _seeded = BuildSeed(TaskStatus.Open, currency, lines: new[] { line }, id: taskId);
        }

        public void SeedCompletedTask() =>
            _seeded = BuildSeed(TaskStatus.Completed, currency: "USD", lines: Array.Empty<TaskLineRecord>());

        public void SeedTaskInStatus(TaskStatus status) =>
            _seeded = BuildSeed(status, currency: "USD", lines: Array.Empty<TaskLineRecord>());

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TaskRecord?> GetAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken)
        {
            if (_seeded is not null && _seeded.Id == taskId)
            {
                return Task.FromResult<TaskRecord?>(_seeded);
            }
            return Task.FromResult<TaskRecord?>(null);
        }

        public Task<IReadOnlyList<TaskSummary>> ListAsync(TaskQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSummary>>(Array.Empty<TaskSummary>());

        public Task<TaskRecord> CreateAsync(
            CompanyId companyId, UserId createdBy, string title, string? description,
            Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate,
            string currencyCode, CancellationToken cancellationToken)
        {
            var created = new TaskRecord
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                TaskNo = "TSK-000001",
                Title = title,
                Description = description,
                CustomerId = customerId,
                ProjectId = projectId,
                AssignedToUserId = assignedToUserId,
                Status = TaskStatus.Open,
                ServiceDate = serviceDate,
                ReadyToBillAtUtc = null,
                BilledInvoiceId = null,
                BilledAtUtc = null,
                TotalBillableValue = 0m,
                TotalDirectCost = 0m,
                CurrencyCode = currencyCode,
                IsVoided = false,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = createdBy,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Lines = Array.Empty<TaskLineRecord>(),
            };
            _seeded = created;
            return Task.FromResult(created);
        }

        public Task<TaskRecord?> UpdateHeaderAsync(
            CompanyId companyId, Guid taskId, string title, string? description,
            Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate,
            CancellationToken cancellationToken)
        {
            if (_seeded is null) return Task.FromResult<TaskRecord?>(null);
            _seeded = _seeded with { Title = title, Description = description };
            return Task.FromResult<TaskRecord?>(_seeded);
        }

        public Task<TaskRecord> AppendLineAsync(
            CompanyId companyId, Guid taskId, Guid itemId, string? description,
            decimal quantity, decimal unitPrice, string currencyCode, Guid? taxCodeId,
            CancellationToken cancellationToken)
        {
            LastAppendedUnitPrice = unitPrice;
            return Task.FromResult(_seeded!);
        }

        public Task<TaskRecord> RemoveLineAsync(CompanyId companyId, Guid taskId, Guid lineId, CancellationToken cancellationToken) =>
            Task.FromResult(_seeded!);

        public Task<TaskRecord?> TransitionStatusAsync(
            CompanyId companyId, Guid taskId, TaskStatus fromStatus, TaskStatus toStatus,
            UserId actorUserId, string? reason, Guid? billedInvoiceId, CancellationToken cancellationToken)
        {
            LastTransition = (fromStatus, toStatus);
            LastBilledInvoiceId = billedInvoiceId;
            if (_seeded is null) return Task.FromResult<TaskRecord?>(null);
            _seeded = _seeded with { Status = toStatus, BilledInvoiceId = billedInvoiceId ?? _seeded.BilledInvoiceId };
            return Task.FromResult<TaskRecord?>(_seeded);
        }

        public Task<IReadOnlyList<TaskStateTransitionRecord>> ListTransitionsAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskStateTransitionRecord>>(Array.Empty<TaskStateTransitionRecord>());

        public Task<IReadOnlyList<TaskSummary>> ListByBilledInvoiceAsync(CompanyId companyId, Guid invoiceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSummary>>(Array.Empty<TaskSummary>());

        private static TaskRecord BuildSeed(TaskStatus status, string currency, IReadOnlyList<TaskLineRecord> lines, Guid? id = null) =>
            new()
            {
                Id = id ?? Guid.NewGuid(),
                CompanyId = CompanyA,
                TaskNo = "TSK-000001",
                Title = "seed",
                Description = null,
                CustomerId = null,
                ProjectId = null,
                AssignedToUserId = null,
                Status = status,
                ServiceDate = new DateOnly(2026, 5, 18),
                ReadyToBillAtUtc = status == TaskStatus.Completed ? DateTimeOffset.UtcNow : null,
                BilledInvoiceId = null,
                BilledAtUtc = status == TaskStatus.Billed ? DateTimeOffset.UtcNow : null,
                TotalBillableValue = lines.Sum(l => l.LineAmount),
                TotalDirectCost = 0m,
                CurrencyCode = currency,
                IsVoided = false,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = Actor,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Lines = lines,
            };
    }
}
