namespace Citus.Modules.Tasks.Domain.Shared;

/// <summary>
/// Full task aggregate — header + lines as read by the workflow and
/// the API. <see cref="TotalBillableValue"/> is the sum of every line's
/// <c>LineAmount</c>. Direct cost rolled up from bill_lines +
/// expense_lines is intentionally NOT cached here — the
/// <c>PostgreSqlTaskMarginReportService</c> computes it live from the
/// joins; caching it on the task header would invite the same drift
/// the H5 dead-column had before its removal.
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

    public required string CurrencyCode { get; init; }

    public required bool IsVoided { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required UserId CreatedBy { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public required IReadOnlyList<TaskLineRecord> Lines { get; init; }
}
