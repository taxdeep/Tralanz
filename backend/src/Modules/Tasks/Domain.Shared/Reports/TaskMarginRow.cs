namespace Citus.Modules.Tasks.Domain.Shared.Reports;

/// <summary>
/// One row of the margin report — a single task with its billable
/// revenue, attributed direct cost, and computed gross margin.
/// <see cref="DirectCost"/> aggregates live from
/// <c>bill_lines.task_id</c> and <c>expense_lines.task_id</c>; it
/// excludes voided bills/expenses but does not net out vendor credits
/// (an opinion v1 takes — vendor credits are rare enough that we'd
/// rather show inflated cost than risk silent under-reporting).
/// </summary>
public sealed record class TaskMarginRow
{
    public required Guid TaskId { get; init; }

    public required string TaskNo { get; init; }

    public required string Title { get; init; }

    public required TaskStatus Status { get; init; }

    public Guid? CustomerId { get; init; }

    public UserId? AssignedToUserId { get; init; }

    public DateOnly? ServiceDate { get; init; }

    public DateTimeOffset? BilledAtUtc { get; init; }

    public Guid? BilledInvoiceId { get; init; }

    public required string CurrencyCode { get; init; }

    /// <summary>Sum of every task line — same as <c>tasks.total_billable_value</c>.</summary>
    public required decimal BillableValue { get; init; }

    /// <summary>Live SUM across bill_lines + expense_lines with this task_id.</summary>
    public required decimal DirectCost { get; init; }

    /// <summary><c>BillableValue - DirectCost</c>.</summary>
    public required decimal GrossMargin { get; init; }

    /// <summary>
    /// <c>GrossMargin / BillableValue</c> as a percentage. Null when
    /// <see cref="BillableValue"/> is zero (avoid divide-by-zero noise
    /// in the UI — the row already shows the loss as a flat number).
    /// </summary>
    public decimal? GrossMarginPercent { get; init; }

    /// <summary>
    /// Company base currency the *Base fields are denominated in. Same
    /// for every row in a single report — the API resolves it from the
    /// active session's company once and stamps it on each row so the
    /// caller doesn't need a side query.
    /// </summary>
    public required string BaseCurrencyCode { get; init; }

    /// <summary>
    /// Resolved FX rate (1 <see cref="CurrencyCode"/> = N
    /// <see cref="BaseCurrencyCode"/>) the service used to translate
    /// this row. <c>1</c> when CurrencyCode == BaseCurrencyCode (no
    /// conversion needed) or when no rate could be resolved (fallback;
    /// see <see cref="FxResolved"/>).
    /// </summary>
    public required decimal FxRate { get; init; }

    /// <summary>
    /// True when a real FX rate row was found for
    /// (CurrencyCode → BaseCurrencyCode, asOf=ServiceDate or earlier);
    /// false when we fell back to <c>FxRate=1</c> because no rate was
    /// available. Lets the UI badge un-converted rows so the operator
    /// knows the totals are approximate.
    /// </summary>
    public required bool FxResolved { get; init; }

    /// <summary><see cref="BillableValue"/> × <see cref="FxRate"/>.</summary>
    public required decimal BillableValueBase { get; init; }

    /// <summary><see cref="DirectCost"/> × <see cref="FxRate"/>.</summary>
    public required decimal DirectCostBase { get; init; }

    /// <summary><see cref="GrossMargin"/> × <see cref="FxRate"/>.</summary>
    public required decimal GrossMarginBase { get; init; }
}
