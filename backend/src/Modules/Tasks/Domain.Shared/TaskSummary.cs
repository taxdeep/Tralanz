namespace Citus.Modules.Tasks.Domain.Shared;

/// <summary>
/// List-row projection — what the topbar search, the task list page,
/// and SmartPicker see. Trimmed down from <see cref="TaskRecord"/> for
/// transport efficiency; no line items, no per-line totals.
/// </summary>
public sealed record class TaskSummary
{
    public required Guid Id { get; init; }

    public required CompanyId CompanyId { get; init; }

    public required string TaskNo { get; init; }

    public required string Title { get; init; }

    public Guid? CustomerId { get; init; }

    public UserId? AssignedToUserId { get; init; }

    public required TaskStatus Status { get; init; }

    public DateOnly? ServiceDate { get; init; }

    public required decimal TotalBillableValue { get; init; }

    public required string CurrencyCode { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
