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
    /// FX rate that translated the BILLABLE side of this row to base.
    /// GL-aligned:
    /// <list type="bullet">
    ///   <item>Billed task → the linked invoice's posted <c>fx_rate</c>
    ///     (the rate Cr Revenue actually hit the ledger at).</item>
    ///   <item>Unbilled task → today's spot rate (projection — there's
    ///     no GL touch yet).</item>
    ///   <item>Task currency == BaseCurrencyCode → 1.</item>
    /// </list>
    /// Cost-side translation does NOT use this rate; each
    /// <c>bill_line</c> / <c>expense_line</c> is converted at its own
    /// parent doc's <c>fx_rate</c> at post time. Hence
    /// <c>DirectCost × FxRate ≠ DirectCostBase</c> in general — read
    /// <see cref="DirectCostBase"/> from the SQL directly, don't
    /// recompute it client-side.
    /// </summary>
    public required decimal FxRate { get; init; }

    /// <summary>
    /// True when a real FX rate was found (invoice's recorded rate for
    /// billed tasks; an <c>fx_rates_daily</c> row for unbilled); false
    /// when the service fell back to <c>FxRate=1</c>. Lets the UI
    /// badge un-converted rows so the operator knows the totals are
    /// approximate. Only describes the BILLABLE side; cost-side
    /// missing rates fall back per-line to the line amount.
    /// </summary>
    public required bool FxResolved { get; init; }

    /// <summary><see cref="BillableValue"/> × <see cref="FxRate"/>.</summary>
    public required decimal BillableValueBase { get; init; }

    /// <summary>
    /// Sum of every cost line's <c>amount × parent_doc.fx_rate</c>.
    /// GL-locked: each posted bill / expense contributed at the rate
    /// stamped on it at post time, never re-translated.
    /// </summary>
    public required decimal DirectCostBase { get; init; }

    /// <summary>
    /// <see cref="BillableValueBase"/> − <see cref="DirectCostBase"/>.
    /// </summary>
    public required decimal GrossMarginBase { get; init; }
}
