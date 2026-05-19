namespace Citus.Modules.Tasks.Domain.Shared.Reports;

/// <summary>
/// Filter parameters for <see cref="ITaskMarginReportService"/>.
/// All filters are optional except <see cref="CompanyId"/> and
/// <see cref="Mode"/> — the former enforces tenant isolation; the
/// latter decides whether the report shows realized or in-flight
/// margin. <see cref="FromDate"/> / <see cref="ToDate"/> filter on
/// <c>service_date</c> for operational mode and on <c>billed_at</c>
/// for billed mode (different time semantics — see <see cref="Mode"/>).
/// </summary>
public sealed record class TaskMarginReportQuery
{
    public required CompanyId CompanyId { get; init; }

    public required TaskMarginReportMode Mode { get; init; }

    public DateOnly? FromDate { get; init; }

    public DateOnly? ToDate { get; init; }

    public Guid? CustomerId { get; init; }

    public UserId? AssignedToUserId { get; init; }

    /// <summary>
    /// Page size cap. Bounded by the service to <c>[1, 500]</c>; values
    /// outside that range get clamped silently. Default 200.
    /// </summary>
    public int Take { get; init; } = 200;

    public int Skip { get; init; } = 0;
}
