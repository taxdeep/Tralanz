using Citus.Modules.Tasks.Application;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using SharedKernel.Identity;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.SysAdmin.Api.Tests;

public class TaskLineLinkValidatorTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);
    private static readonly UserId Actor = UserId.Parse("U000001");
    private static readonly Guid TaskId = Guid.NewGuid();

    [Fact]
    public async Task Validate_passes_when_task_is_open()
    {
        var validator = new TaskLineLinkValidator(new StubStore(TaskStatus.Open));
        await validator.ValidateAsync(CompanyA, TaskId, CancellationToken.None);
    }

    [Fact]
    public async Task Validate_passes_when_task_is_completed()
    {
        var validator = new TaskLineLinkValidator(new StubStore(TaskStatus.Completed));
        await validator.ValidateAsync(CompanyA, TaskId, CancellationToken.None);
    }

    [Fact]
    public async Task Validate_rejects_billed_task()
    {
        var validator = new TaskLineLinkValidator(new StubStore(TaskStatus.Billed));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(CompanyA, TaskId, CancellationToken.None));
        Assert.Contains("billed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_rejects_canceled_task()
    {
        var validator = new TaskLineLinkValidator(new StubStore(TaskStatus.Canceled));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(CompanyA, TaskId, CancellationToken.None));
        Assert.Contains("canceled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_rejects_unknown_task_or_cross_company()
    {
        // Store returns null when the (company, task) pair isn't in
        // the store — exactly the same path a cross-company lookup
        // would hit, so we cover both with the same test.
        var validator = new TaskLineLinkValidator(new EmptyStore());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(CompanyA, TaskId, CancellationToken.None));
        Assert.Contains("not permitted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_rejects_empty_task_id()
    {
        var validator = new TaskLineLinkValidator(new EmptyStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(CompanyA, Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Validate_rejects_unset_company()
    {
        var validator = new TaskLineLinkValidator(new EmptyStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(default, TaskId, CancellationToken.None));
    }

    private sealed class StubStore(TaskStatus status) : EmptyStore
    {
        public override Task<TaskRecord?> GetAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<TaskRecord?>(new TaskRecord
            {
                Id = taskId,
                CompanyId = companyId,
                TaskNo = "TSK-000001",
                Title = "stub",
                Status = status,
                CurrencyCode = "USD",
                TotalBillableValue = 0m,
                IsVoided = false,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = Actor,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Lines = Array.Empty<TaskLineRecord>(),
            });
    }

    private class EmptyStore : ITaskStore
    {
        public virtual Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public virtual Task<TaskRecord?> GetAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<TaskRecord?>(null);
        public virtual Task<IReadOnlyList<TaskSummary>> ListAsync(TaskQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSummary>>(Array.Empty<TaskSummary>());
        public virtual Task<TaskRecord> CreateAsync(CompanyId companyId, UserId createdBy, string title, string? description, Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate, string currencyCode, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public virtual Task<TaskRecord?> UpdateHeaderAsync(CompanyId companyId, Guid taskId, string title, string? description, Guid? customerId, Guid? projectId, UserId? assignedToUserId, DateOnly? serviceDate, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public virtual Task<TaskRecord> AppendLineAsync(CompanyId companyId, Guid taskId, Guid itemId, string? description, decimal quantity, decimal unitPrice, string currencyCode, Guid? taxCodeId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public virtual Task<TaskRecord> RemoveLineAsync(CompanyId companyId, Guid taskId, Guid lineId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public virtual Task<TaskRecord?> TransitionStatusAsync(CompanyId companyId, Guid taskId, TaskStatus fromStatus, TaskStatus toStatus, UserId actorUserId, string? reason, Guid? billedInvoiceId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public virtual Task<IReadOnlyList<TaskStateTransitionRecord>> ListTransitionsAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskStateTransitionRecord>>(Array.Empty<TaskStateTransitionRecord>());
        public virtual Task<IReadOnlyList<TaskSummary>> ListByBilledInvoiceAsync(CompanyId companyId, Guid invoiceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSummary>>(Array.Empty<TaskSummary>());
        public virtual Task<IReadOnlyList<TaskDisplayLookup>> LookupDisplayAsync(CompanyId companyId, IReadOnlyList<Guid> taskIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskDisplayLookup>>(Array.Empty<TaskDisplayLookup>());
    }
}
