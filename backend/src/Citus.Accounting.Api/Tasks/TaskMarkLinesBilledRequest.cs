namespace Citus.Accounting.Api.Tasks;

public sealed record TaskMarkLinesBilledRequest(
    string? SourceType,
    Guid SourceId,
    Guid? CustomerId,
    IReadOnlyList<TaskMarkLineBilledRequestLine> Lines);

public sealed record TaskMarkLineBilledRequestLine(
    Guid TaskId,
    Guid TaskLineId,
    Guid SourceLineId);
