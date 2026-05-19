namespace Citus.Modules.Tasks.Domain.Shared;

public sealed record class TaskStateTransitionRecord(
    long Id,
    CompanyId CompanyId,
    Guid TaskId,
    TaskStatus FromStatus,
    TaskStatus ToStatus,
    string? Reason,
    UserId ActorUserId,
    DateTimeOffset OccurredAtUtc);
