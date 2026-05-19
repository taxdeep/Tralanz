namespace Citus.Modules.Tasks.Application.Contracts;

/// <summary>
/// Header-only edit. Only callable while the task is
/// <see cref="Domain.Shared.TaskStatus.Open"/> — workflow rejects
/// edits on completed / billed / canceled tasks.
/// Currency cannot be changed once any line exists (workflow rule).
/// </summary>
public sealed record class TaskUpdateRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public Guid? CustomerId { get; init; }

    public Guid? ProjectId { get; init; }

    public UserId? AssignedToUserId { get; init; }

    public DateOnly? ServiceDate { get; init; }
}
