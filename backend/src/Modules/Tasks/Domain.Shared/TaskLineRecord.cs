namespace Citus.Modules.Tasks.Domain.Shared;

/// <summary>
/// One billable line inside a task. <see cref="UnitPrice"/> is the
/// authoritative price the AR invoice will inherit when the task
/// bills — taken from the item-price resolver at add time, or
/// supplied manually if the caller chose to override.
/// </summary>
public sealed record class TaskLineRecord(
    Guid Id,
    CompanyId CompanyId,
    Guid TaskId,
    int LineNo,
    Guid ItemId,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    string CurrencyCode,
    decimal LineAmount,
    Guid? TaxCodeId);
