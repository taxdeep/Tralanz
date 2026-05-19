using Citus.Modules.Tasks.Domain.Shared;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.Modules.Tasks.Application.Contracts;

/// <summary>
/// Filter for the list endpoint. <see cref="OnlyAssignedToUserId"/>
/// drives the per-user view when the caller lacks <c>task.view.all</c>:
/// the API layer fills this in unconditionally to that user's id so
/// the SQL never returns rows assigned to anyone else.
/// </summary>
public sealed record class TaskQuery
{
    public required CompanyId CompanyId { get; init; }

    public TaskStatus? Status { get; init; }

    public Guid? CustomerId { get; init; }

    public UserId? OnlyAssignedToUserId { get; init; }

    public int Take { get; init; } = 50;

    public int Skip { get; init; } = 0;
}
