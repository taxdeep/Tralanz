using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.UnitySearch.Application.Contracts;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.Modules.Tasks.Application;

public sealed class TaskWorkflow(
    ITaskStore store,
    IItemPriceResolver priceResolver,
    IUnitySearchProjectionStore projectionStore) : ITaskWorkflow
{
    public Task<TaskRecord?> GetAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireTaskId(taskId);
        return store.GetAsync(companyId, taskId, cancellationToken);
    }

    public Task<IReadOnlyList<TaskSummary>> ListAsync(
        TaskQuery query,
        CancellationToken cancellationToken)
    {
        RequireCompany(query.CompanyId);
        return store.ListAsync(query, cancellationToken);
    }

    public async Task<TaskRecord> CreateAsync(
        CompanyId companyId,
        UserId actorUserId,
        TaskCreateRequest request,
        CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireActor(actorUserId);

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            throw new InvalidOperationException("Task title is required.");
        }

        var currency = NormalizeCurrency(request.CurrencyCode);

        var serviceDate = request.ServiceDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var created = await store.CreateAsync(
            companyId,
            actorUserId,
            title,
            NormalizeOptionalText(request.Description),
            request.CustomerId,
            request.ProjectId,
            request.AssignedToUserId,
            request.ServiceDate,
            currency,
            cancellationToken);

        // Add any seed lines the caller provided. Each line goes
        // through the same AddLineAsync path so the workflow's
        // price-resolution and currency rules apply uniformly.
        var current = created;
        foreach (var line in request.Lines)
        {
            current = await AddLineToOpenTaskAsync(
                companyId,
                current,
                actorUserId,
                line,
                serviceDate,
                cancellationToken);
        }

        await InvalidateSearchAsync(companyId, cancellationToken);
        return current;
    }

    public async Task<TaskRecord> UpdateAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        TaskUpdateRequest request,
        CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireTaskId(taskId);
        RequireActor(actorUserId);

        var existing = await RequireOpenAsync(companyId, taskId, cancellationToken);

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            throw new InvalidOperationException("Task title is required.");
        }

        var updated = await store.UpdateHeaderAsync(
            companyId,
            taskId,
            title,
            NormalizeOptionalText(request.Description),
            request.CustomerId,
            request.ProjectId,
            request.AssignedToUserId,
            request.ServiceDate,
            cancellationToken)
            ?? throw new InvalidOperationException($"Task '{taskId:D}' disappeared during update.");

        await InvalidateSearchAsync(companyId, cancellationToken);
        return updated;
    }

    public async Task<TaskRecord> AddLineAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        TaskLineUpsertRequest request,
        CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireTaskId(taskId);
        RequireActor(actorUserId);

        var existing = await RequireOpenAsync(companyId, taskId, cancellationToken);
        var serviceDate = existing.ServiceDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var updated = await AddLineToOpenTaskAsync(companyId, existing, actorUserId, request, serviceDate, cancellationToken);
        await InvalidateSearchAsync(companyId, cancellationToken);
        return updated;
    }

    public async Task<TaskRecord> RemoveLineAsync(
        CompanyId companyId,
        Guid taskId,
        Guid lineId,
        UserId actorUserId,
        CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireTaskId(taskId);
        RequireActor(actorUserId);

        if (lineId == Guid.Empty)
        {
            throw new InvalidOperationException("Line id is required to remove a task line.");
        }

        _ = await RequireOpenAsync(companyId, taskId, cancellationToken);

        var updated = await store.RemoveLineAsync(companyId, taskId, lineId, cancellationToken);
        await InvalidateSearchAsync(companyId, cancellationToken);
        return updated;
    }

    public async Task<TaskRecord> CompleteAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireTaskId(taskId);
        RequireActor(actorUserId);

        var existing = await RequireExistingAsync(companyId, taskId, cancellationToken);
        if (existing.Status != TaskStatus.Open)
        {
            throw new InvalidOperationException(
                $"Task is in status '{existing.Status.ToToken()}' and cannot be completed; only open tasks may be completed.");
        }

        if (existing.Lines.Count == 0)
        {
            throw new InvalidOperationException("Cannot complete a task with no billable lines.");
        }

        return await TransitionOrThrowAsync(
            companyId,
            taskId,
            TaskStatus.Open,
            TaskStatus.Completed,
            actorUserId,
            reason,
            billedInvoiceId: null,
            cancellationToken);
    }

    public async Task<TaskRecord> CancelAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireTaskId(taskId);
        RequireActor(actorUserId);

        var existing = await RequireExistingAsync(companyId, taskId, cancellationToken);
        if (existing.Status is not (TaskStatus.Open or TaskStatus.Completed))
        {
            throw new InvalidOperationException(
                $"Task is in status '{existing.Status.ToToken()}' and cannot be cancelled. Only open or completed tasks may be cancelled.");
        }

        return await TransitionOrThrowAsync(
            companyId,
            taskId,
            existing.Status,
            TaskStatus.Canceled,
            actorUserId,
            reason,
            billedInvoiceId: null,
            cancellationToken);
    }

    public async Task<TaskRecord> MarkBilledAsync(
        CompanyId companyId,
        Guid taskId,
        Guid invoiceId,
        UserId actorUserId,
        CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireTaskId(taskId);
        RequireActor(actorUserId);
        if (invoiceId == Guid.Empty)
        {
            throw new InvalidOperationException("Invoice id is required when marking a task billed.");
        }

        var existing = await RequireExistingAsync(companyId, taskId, cancellationToken);
        if (existing.Status != TaskStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Task is in status '{existing.Status.ToToken()}' and cannot be marked billed; only completed tasks may bill.");
        }

        return await TransitionOrThrowAsync(
            companyId,
            taskId,
            TaskStatus.Completed,
            TaskStatus.Billed,
            actorUserId,
            $"Billed by AR invoice {invoiceId:D}.",
            invoiceId,
            cancellationToken);
    }

    public async Task<TaskRecord> RestoreFromBilledAsync(
        CompanyId companyId,
        Guid taskId,
        UserId actorUserId,
        string? reason,
        CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        RequireTaskId(taskId);
        RequireActor(actorUserId);

        var existing = await RequireExistingAsync(companyId, taskId, cancellationToken);
        if (existing.Status != TaskStatus.Billed)
        {
            throw new InvalidOperationException(
                $"Task is in status '{existing.Status.ToToken()}' and cannot be restored to completed; only billed tasks may be rolled back.");
        }

        return await TransitionOrThrowAsync(
            companyId,
            taskId,
            TaskStatus.Billed,
            TaskStatus.Completed,
            actorUserId,
            reason,
            billedInvoiceId: null,
            cancellationToken);
    }

    private async Task<TaskRecord> AddLineToOpenTaskAsync(
        CompanyId companyId,
        TaskRecord task,
        UserId actorUserId,
        TaskLineUpsertRequest request,
        DateOnly serviceDate,
        CancellationToken cancellationToken)
    {
        if (request.ItemId == Guid.Empty)
        {
            throw new InvalidOperationException("Item id is required to add a task line.");
        }

        if (request.Quantity <= 0m)
        {
            throw new InvalidOperationException("Line quantity must be positive.");
        }

        decimal unitPrice;
        if (request.UnitPrice.HasValue)
        {
            if (request.UnitPrice.Value < 0m)
            {
                throw new InvalidOperationException("Unit price cannot be negative.");
            }
            unitPrice = request.UnitPrice.Value;
        }
        else
        {
            // Resolver is opt-in for everyone except Task: here we
            // require it (callers without a manual price expect us
            // to look one up). If nothing matches, surface a clear
            // error instead of writing 0.
            var resolution = await priceResolver.ResolveAsync(
                new InventoryItemPriceQuery
                {
                    CompanyId = companyId,
                    ItemId = request.ItemId,
                    CurrencyCode = task.CurrencyCode,
                    AsOf = serviceDate,
                    CustomerId = task.CustomerId,
                    Quantity = request.Quantity,
                },
                cancellationToken);

            unitPrice = resolution?.UnitPrice
                ?? throw new InvalidOperationException(
                    $"No price was found for item '{request.ItemId:D}' in currency '{task.CurrencyCode}' as of {serviceDate:yyyy-MM-dd}. Supply UnitPrice explicitly.");
        }

        return await store.AppendLineAsync(
            companyId,
            task.Id,
            request.ItemId,
            NormalizeOptionalText(request.Description),
            request.Quantity,
            unitPrice,
            task.CurrencyCode,
            request.TaxCodeId,
            cancellationToken);
    }

    private async Task<TaskRecord> RequireExistingAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken) =>
        await store.GetAsync(companyId, taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Task '{taskId:D}' was not found in the active company.");

    private async Task<TaskRecord> RequireOpenAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var existing = await RequireExistingAsync(companyId, taskId, cancellationToken);
        if (existing.Status != TaskStatus.Open)
        {
            throw new InvalidOperationException(
                $"Task is in status '{existing.Status.ToToken()}' and is no longer editable. " +
                "Edits are accepted only while a task is open.");
        }
        return existing;
    }

    private async Task<TaskRecord> TransitionOrThrowAsync(
        CompanyId companyId,
        Guid taskId,
        TaskStatus fromStatus,
        TaskStatus toStatus,
        UserId actorUserId,
        string? reason,
        Guid? billedInvoiceId,
        CancellationToken cancellationToken)
    {
        var result = await store.TransitionStatusAsync(
            companyId,
            taskId,
            fromStatus,
            toStatus,
            actorUserId,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            billedInvoiceId,
            cancellationToken);

        if (result is null)
        {
            throw new InvalidOperationException(
                $"Task '{taskId:D}' could not be transitioned from '{fromStatus.ToToken()}' to '{toStatus.ToToken()}' — it may have already moved.");
        }

        await InvalidateSearchAsync(companyId, cancellationToken);
        return result;
    }

    /// <summary>
    /// Drops the company's UnitySearch projection so the next search /
    /// picker call rebuilds it on the spot rather than waiting out the
    /// 5-minute refresh window. Best-effort by design — failures here
    /// are logged but never rolled back. Worst case: the new / changed
    /// task appears in search up to 5 minutes later.
    /// </summary>
    private Task InvalidateSearchAsync(CompanyId companyId, CancellationToken cancellationToken) =>
        projectionStore.InvalidateAsync(companyId, cancellationToken);

    private static void RequireCompany(CompanyId companyId)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required for task workflows.");
        }
    }

    private static void RequireTaskId(Guid taskId)
    {
        if (taskId == Guid.Empty)
        {
            throw new InvalidOperationException("Task id is required.");
        }
    }

    private static void RequireActor(UserId actorUserId)
    {
        if (actorUserId.Value is null)
        {
            throw new InvalidOperationException("An acting user is required for task workflows.");
        }
    }

    private static string NormalizeCurrency(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Currency code is required on the task.");
        }
        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length != 3)
        {
            throw new InvalidOperationException($"Currency code must be a 3-letter ISO code; got '{code}'.");
        }
        return normalized;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }
}
