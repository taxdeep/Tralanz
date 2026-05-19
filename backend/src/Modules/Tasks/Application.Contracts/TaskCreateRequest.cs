using Citus.Modules.Tasks.Domain.Shared;

namespace Citus.Modules.Tasks.Application.Contracts;

/// <summary>
/// Input for <c>ITaskWorkflow.CreateAsync</c>. The task always lands
/// in <see cref="TaskStatus.Open"/>; <see cref="Lines"/> may be empty
/// and added later via <c>AddLineAsync</c>.
/// </summary>
public sealed record class TaskCreateRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public Guid? CustomerId { get; init; }

    public Guid? ProjectId { get; init; }

    public UserId? AssignedToUserId { get; init; }

    public DateOnly? ServiceDate { get; init; }

    /// <summary>Document currency for every line on this task.</summary>
    public required string CurrencyCode { get; init; }

    public IReadOnlyList<TaskLineUpsertRequest> Lines { get; init; } = Array.Empty<TaskLineUpsertRequest>();
}
