namespace Citus.Modules.Tasks.Domain.Shared;

/// <summary>
/// Full task aggregate — header + lines as read by the workflow and
/// the API. <see cref="TotalBillableValue"/> is the sum of every line's
/// <c>LineAmount</c>; <see cref="TotalDirectCost"/> is fed by AP postings
/// (Batch 8+ wires the line-level <c>task_id</c> links and the read
/// model). For now it stays at 0 — the column exists so AP can write
/// into it later without a follow-up migration.
/// </summary>
public sealed record class TaskRecord
{
    public required Guid Id { get; init; }

    public required CompanyId CompanyId { get; init; }

    public required string TaskNo { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public Guid? CustomerId { get; init; }

    public Guid? ProjectId { get; init; }

    public UserId? AssignedToUserId { get; init; }

    public required TaskStatus Status { get; init; }

    public DateOnly? ServiceDate { get; init; }

    public DateTimeOffset? ReadyToBillAtUtc { get; init; }

    public Guid? BilledInvoiceId { get; init; }

    public DateTimeOffset? BilledAtUtc { get; init; }

    public required decimal TotalBillableValue { get; init; }

    public required decimal TotalDirectCost { get; init; }

    public required string CurrencyCode { get; init; }

    public required bool IsVoided { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required UserId CreatedBy { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public required IReadOnlyList<TaskLineRecord> Lines { get; init; }
}
