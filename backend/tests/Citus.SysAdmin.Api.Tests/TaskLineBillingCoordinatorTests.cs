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

/// <summary>
/// H6-2: line-level billing path tests. Exercises
/// <see cref="ITaskBillingCoordinator.MarkLinesAsBilledAsync"/> +
/// <see cref="ITaskWorkflow.RecomputeAndTransitionFromLinesAsync"/> end
/// to end against an in-memory store that models task_lines.
/// </summary>
public class TaskLineBillingCoordinatorTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);
    private static readonly UserId Actor = UserId.Parse("U000001");

    [Fact]
    public async Task Partial_invoice_moves_task_from_completed_to_partially_billed()
    {
        var store = new LineAwareStore();
        var (taskId, lineIds) = store.SeedCompletedTaskWithLines(lineCount: 3);
        var coordinator = MakeCoordinator(store);

        var invoiceId = Guid.NewGuid();
        var result = await coordinator.MarkLinesAsBilledAsync(
            CompanyA,
            sourceType: "invoice",
            sourceId: invoiceId,
            customerId: null,
            new[] { new TaskLineBillingMapping(taskId, lineIds[0], SourceLineId: Guid.NewGuid()) },
            Actor,
            CancellationToken.None);

        Assert.Single(result.ProcessedTasks);
        Assert.Equal(TaskStatus.PartiallyBilled, store.GetStatus(taskId));
        // First line stamped, the other two still unstamped.
        Assert.Equal(("invoice", invoiceId), store.GetLineStamp(lineIds[0]));
        Assert.Null(store.GetLineStamp(lineIds[1]).SourceType);
        Assert.Null(store.GetLineStamp(lineIds[2]).SourceType);
        // PartiallyBilled is not a terminal-billed state, so the task
        // header's billed_invoice_id stays null until the final flip.
        Assert.Null(store.GetBilledInvoiceId(taskId));
    }

    [Fact]
    public async Task Second_invoice_covering_last_line_flips_to_billed_and_stamps_header()
    {
        var store = new LineAwareStore();
        var (taskId, lineIds) = store.SeedCompletedTaskWithLines(lineCount: 2);
        var coordinator = MakeCoordinator(store);

        // First bill covers line 0 → PartiallyBilled.
        await coordinator.MarkLinesAsBilledAsync(
            CompanyA, "invoice", Guid.NewGuid(), null,
            new[] { new TaskLineBillingMapping(taskId, lineIds[0], null) },
            Actor, CancellationToken.None);

        Assert.Equal(TaskStatus.PartiallyBilled, store.GetStatus(taskId));

        // Second bill covers the last line → Billed, billed_invoice_id stamped.
        var finalInvoice = Guid.NewGuid();
        var result = await coordinator.MarkLinesAsBilledAsync(
            CompanyA, "invoice", finalInvoice, null,
            new[] { new TaskLineBillingMapping(taskId, lineIds[1], null) },
            Actor, CancellationToken.None);

        Assert.Single(result.ProcessedTasks);
        Assert.Equal(TaskStatus.Billed, store.GetStatus(taskId));
        Assert.Equal(finalInvoice, store.GetBilledInvoiceId(taskId));
    }

    [Fact]
    public async Task Idempotent_resubmission_of_same_source_returns_skipped_outcome()
    {
        var store = new LineAwareStore();
        var (taskId, lineIds) = store.SeedCompletedTaskWithLines(lineCount: 2);
        var coordinator = MakeCoordinator(store);
        var invoiceId = Guid.NewGuid();

        var first = await coordinator.MarkLinesAsBilledAsync(
            CompanyA, "invoice", invoiceId, null,
            new[] { new TaskLineBillingMapping(taskId, lineIds[0], null) },
            Actor, CancellationToken.None);
        Assert.Single(first.ProcessedTasks);
        Assert.Equal(TaskStatus.PartiallyBilled, store.GetStatus(taskId));

        // Re-call with the same (source, line) pair. Should be a no-op
        // — same outcome shape, but the task lands in SkippedTasks
        // because no transition fired.
        var second = await coordinator.MarkLinesAsBilledAsync(
            CompanyA, "invoice", invoiceId, null,
            new[] { new TaskLineBillingMapping(taskId, lineIds[0], null) },
            Actor, CancellationToken.None);
        Assert.Empty(second.ProcessedTasks);
        Assert.Single(second.SkippedTasks);
        Assert.Equal(TaskStatus.PartiallyBilled, store.GetStatus(taskId));
    }

    [Fact]
    public async Task Cross_customer_mapping_is_refused_pre_flight()
    {
        var store = new LineAwareStore();
        var customerA = Guid.NewGuid();
        var customerB = Guid.NewGuid();
        var (taskId, lineIds) = store.SeedCompletedTaskWithLines(lineCount: 1, customerId: customerA);
        var coordinator = MakeCoordinator(store);

        // Pass customerB to the coordinator; task is on customerA.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkLinesAsBilledAsync(
                CompanyA, "invoice", Guid.NewGuid(), customerB,
                new[] { new TaskLineBillingMapping(taskId, lineIds[0], null) },
                Actor, CancellationToken.None));

        // Refused at pre-flight: no write happened, status unchanged.
        Assert.Equal(TaskStatus.Completed, store.GetStatus(taskId));
        Assert.Null(store.GetLineStamp(lineIds[0]).SourceType);
    }

    [Fact]
    public async Task Cancelled_task_refuses_line_marking()
    {
        var store = new LineAwareStore();
        var (taskId, lineIds) = store.SeedCompletedTaskWithLines(lineCount: 1);
        store.ForceStatus(taskId, TaskStatus.Canceled);
        var coordinator = MakeCoordinator(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkLinesAsBilledAsync(
                CompanyA, "invoice", Guid.NewGuid(), null,
                new[] { new TaskLineBillingMapping(taskId, lineIds[0], null) },
                Actor, CancellationToken.None));
    }

    [Fact]
    public async Task Different_source_billing_same_line_raises()
    {
        var store = new LineAwareStore();
        var (taskId, lineIds) = store.SeedCompletedTaskWithLines(lineCount: 1);
        var coordinator = MakeCoordinator(store);

        var firstInvoice = Guid.NewGuid();
        await coordinator.MarkLinesAsBilledAsync(
            CompanyA, "invoice", firstInvoice, null,
            new[] { new TaskLineBillingMapping(taskId, lineIds[0], null) },
            Actor, CancellationToken.None);

        // Try to re-bill the same task_line via a DIFFERENT invoice.
        // The store's MarkLineBilledAsync surfaces this loud; the
        // coordinator does not catch it (it's a data-integrity signal).
        var secondInvoice = Guid.NewGuid();

        // Need to seed task back to a state that passes the pre-flight
        // gate, otherwise we'd just fail on the status check first.
        store.ForceStatus(taskId, TaskStatus.PartiallyBilled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkLinesAsBilledAsync(
                CompanyA, "invoice", secondInvoice, null,
                new[] { new TaskLineBillingMapping(taskId, lineIds[0], null) },
                Actor, CancellationToken.None));
    }

    [Fact]
    public async Task Sales_receipt_source_marks_line_but_leaves_billed_invoice_id_null()
    {
        var store = new LineAwareStore();
        var (taskId, lineIds) = store.SeedCompletedTaskWithLines(lineCount: 1);
        var coordinator = MakeCoordinator(store);

        var receiptId = Guid.NewGuid();
        await coordinator.MarkLinesAsBilledAsync(
            CompanyA, "sales_receipt", receiptId, null,
            new[] { new TaskLineBillingMapping(taskId, lineIds[0], null) },
            Actor, CancellationToken.None);

        // Single-line task → all lines billed → terminal Billed status.
        Assert.Equal(TaskStatus.Billed, store.GetStatus(taskId));
        // But the source is sales_receipt, not an invoice — the header's
        // billed_invoice_id should NOT be stamped (it's an AR-invoice-
        // specific field). Line stamp carries the actual source identity.
        Assert.Null(store.GetBilledInvoiceId(taskId));
        Assert.Equal(("sales_receipt", receiptId), store.GetLineStamp(lineIds[0]));
    }

    private static TaskBillingCoordinator MakeCoordinator(LineAwareStore store)
    {
        var workflow = new TaskWorkflow(store, NullResolver.Instance, NullProjection.Instance);
        return new TaskBillingCoordinator(store, workflow);
    }

    // -----------------------------------------------------------------
    // Test doubles: an in-memory ITaskStore that actually models
    // task_lines so the new H6-2 status-recompute logic runs end-to-end.
    // -----------------------------------------------------------------

    private sealed class LineAwareStore : ITaskStore
    {
        private readonly Dictionary<Guid, TaskRecord> _tasks = new();
        private readonly Dictionary<Guid, List<TaskLineRecord>> _linesByTask = new();
        private readonly Dictionary<Guid, (string SourceType, Guid SourceId, Guid? SourceLineId, DateTimeOffset BilledAtUtc)> _stamps = new();

        public (Guid TaskId, Guid[] LineIds) SeedCompletedTaskWithLines(int lineCount, Guid? customerId = null)
        {
            var taskId = Guid.NewGuid();
            var lineIds = new Guid[lineCount];
            var lines = new List<TaskLineRecord>(lineCount);
            for (var i = 0; i < lineCount; i++)
            {
                var lineId = Guid.NewGuid();
                lineIds[i] = lineId;
                lines.Add(new TaskLineRecord(
                    Id: lineId,
                    CompanyId: CompanyA,
                    TaskId: taskId,
                    LineNo: i + 1,
                    ItemId: Guid.NewGuid(),
                    Description: $"line {i + 1}",
                    Quantity: 1m,
                    UnitPrice: 100m,
                    CurrencyCode: "USD",
                    LineAmount: 100m,
                    TaxCodeId: null));
            }
            _linesByTask[taskId] = lines;
            _tasks[taskId] = new TaskRecord
            {
                Id = taskId,
                CompanyId = CompanyA,
                TaskNo = $"TSK-{_tasks.Count + 1:D6}",
                Title = "seed",
                CustomerId = customerId,
                Status = TaskStatus.Completed,
                CurrencyCode = "USD",
                TotalBillableValue = lineCount * 100m,
                IsVoided = false,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = Actor,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Lines = lines,
            };
            return (taskId, lineIds);
        }

        public TaskStatus GetStatus(Guid taskId) => _tasks[taskId].Status;

        public Guid? GetBilledInvoiceId(Guid taskId) => _tasks[taskId].BilledInvoiceId;

        public (string? SourceType, Guid? SourceId) GetLineStamp(Guid taskLineId)
        {
            if (!_stamps.TryGetValue(taskLineId, out var stamp))
            {
                return (null, null);
            }
            return (stamp.SourceType, stamp.SourceId);
        }

        public void ForceStatus(Guid taskId, TaskStatus status)
        {
            _tasks[taskId] = _tasks[taskId] with { Status = status };
        }

        // -- ITaskStore --

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TaskRecord?> GetAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult(_tasks.TryGetValue(taskId, out var t) && t.CompanyId == companyId ? t : null);

        public Task<IReadOnlyList<TaskSummary>> ListAsync(TaskQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSummary>>(Array.Empty<TaskSummary>());

        public Task<TaskRecord> CreateAsync(CompanyId companyId, UserId createdBy, string title, string? description, Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate, string currencyCode, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TaskRecord?> UpdateHeaderAsync(CompanyId companyId, Guid taskId, string title, string? description, Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TaskRecord> AppendLineAsync(CompanyId companyId, Guid taskId, Guid itemId, string? description, decimal quantity, decimal unitPrice, string currencyCode, Guid? taxCodeId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TaskRecord> RemoveLineAsync(CompanyId companyId, Guid taskId, Guid lineId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TaskRecord?> TransitionStatusAsync(
            CompanyId companyId, Guid taskId, TaskStatus fromStatus, TaskStatus toStatus,
            UserId actorUserId, string? reason, Guid? billedInvoiceId, CancellationToken cancellationToken)
        {
            if (!_tasks.TryGetValue(taskId, out var t)) return Task.FromResult<TaskRecord?>(null);
            if (t.Status != fromStatus)
            {
                throw new InvalidOperationException(
                    $"Concurrent transition: expected '{fromStatus}', found '{t.Status}'.");
            }
            var updated = t with
            {
                Status = toStatus,
                BilledInvoiceId = billedInvoiceId ?? t.BilledInvoiceId,
                BilledAtUtc = toStatus == TaskStatus.Billed ? DateTimeOffset.UtcNow : t.BilledAtUtc,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _tasks[taskId] = updated;
            return Task.FromResult<TaskRecord?>(updated);
        }

        public Task<IReadOnlyList<TaskStateTransitionRecord>> ListTransitionsAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskStateTransitionRecord>>(Array.Empty<TaskStateTransitionRecord>());

        public Task<IReadOnlyList<TaskSummary>> ListByBilledInvoiceAsync(CompanyId companyId, Guid invoiceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSummary>>(Array.Empty<TaskSummary>());

        public Task<IReadOnlyList<TaskDisplayLookup>> LookupDisplayAsync(CompanyId companyId, IReadOnlyList<Guid> taskIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskDisplayLookup>>(Array.Empty<TaskDisplayLookup>());

        public Task<TaskLineBillingStampOutcome> MarkLineBilledAsync(
            CompanyId companyId, Guid taskLineId, string sourceType, Guid sourceId,
            Guid? sourceLineId, DateTimeOffset billedAtUtc, CancellationToken cancellationToken)
        {
            if (_stamps.TryGetValue(taskLineId, out var existing))
            {
                if (existing.SourceType == sourceType && existing.SourceId == sourceId)
                {
                    return Task.FromResult(new TaskLineBillingStampOutcome(false, sourceType, sourceId));
                }
                throw new InvalidOperationException(
                    $"Task line '{taskLineId:D}' is already billed by {existing.SourceType} '{existing.SourceId:D}' " +
                    $"and cannot be re-billed by {sourceType} '{sourceId:D}'.");
            }
            _stamps[taskLineId] = (sourceType, sourceId, sourceLineId, billedAtUtc);
            return Task.FromResult(new TaskLineBillingStampOutcome(true, sourceType, sourceId));
        }

        public Task<TaskLineBillingSnapshot?> ReadLineBillingSnapshotAsync(
            CompanyId companyId, Guid taskId, CancellationToken cancellationToken)
        {
            if (!_tasks.TryGetValue(taskId, out var t) || t.CompanyId != companyId)
            {
                return Task.FromResult<TaskLineBillingSnapshot?>(null);
            }
            var lines = _linesByTask.TryGetValue(taskId, out var ls) ? ls : new List<TaskLineRecord>();
            var billedCount = lines.Count(l => _stamps.ContainsKey(l.Id));
            return Task.FromResult<TaskLineBillingSnapshot?>(new TaskLineBillingSnapshot(
                TaskId: taskId,
                CustomerId: t.CustomerId,
                CurrentStatus: t.Status,
                TotalLineCount: lines.Count,
                BilledLineCount: billedCount));
        }
    }

    private sealed class NullResolver : IItemPriceResolver
    {
        public static readonly NullResolver Instance = new();
        public Task<InventoryItemPriceResolution?> ResolveAsync(InventoryItemPriceQuery query, CancellationToken ct) =>
            Task.FromResult<InventoryItemPriceResolution?>(null);
    }

    private sealed class NullProjection : IUnitySearchProjectionStore
    {
        public static readonly NullProjection Instance = new();

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
}
