using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.Tasks.Domain.Shared.Reports;
using Modules.CompanyAccess.Memberships;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.SysAdmin.Api.Tests;

/// <summary>
/// Batch 10: read-model wiring contracts. The Postgres SQL itself is
/// covered by integration tests against a real DB; here we pin the
/// in-process invariants (permission catalog, preset membership, mode
/// parsing, default query shape).
/// </summary>
public class TaskMarginReportContractTests
{
    [Fact]
    public void Permission_token_is_registered_in_the_catalog()
    {
        Assert.Contains("task.report.margin", CompanyMembershipPermissionCatalog.AllTokens);
        Assert.Equal("task.report.margin", CompanyMembershipPermissionCatalog.TaskReportMargin);
    }

    [Fact]
    public void Owner_preset_includes_margin_report_token()
    {
        var tokens = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Owner);
        Assert.Contains(CompanyMembershipPermissionCatalog.TaskReportMargin, tokens);
    }

    [Fact]
    public void Accountant_preset_includes_margin_report_token()
    {
        // Accountants own gross-margin analysis even when the Task
        // module's day-to-day workflow is scoped to PMs.
        var tokens = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Accountant);
        Assert.Contains(CompanyMembershipPermissionCatalog.TaskReportMargin, tokens);
    }

    [Fact]
    public void Sales_preset_includes_margin_report_token()
    {
        // Sales care whether the work they are scoping is actually
        // profitable; pin this so a future preset refactor doesn't
        // silently strip it.
        var tokens = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Sales);
        Assert.Contains(CompanyMembershipPermissionCatalog.TaskReportMargin, tokens);
    }

    [Fact]
    public void Bookkeeper_preset_does_not_include_margin_report_token()
    {
        // Bookkeeper is intentionally cost-only — margin is a revenue
        // conversation. Pin the exclusion so a future broad sweep
        // doesn't accidentally widen the role.
        var tokens = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Bookkeeper);
        Assert.DoesNotContain(CompanyMembershipPermissionCatalog.TaskReportMargin, tokens);
    }

    [Fact]
    public void Viewer_preset_does_not_currently_include_margin_report_token()
    {
        // Pinning the current behavior, not an opinion: the viewer
        // sweep matches *.view / *.read tokens; margin tokens are
        // *.margin and don't match. If a future product call wants
        // viewers to see margin, update the sweep regex AND this
        // test together (forces explicit re-confirmation).
        var tokens = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Viewer);
        Assert.DoesNotContain(CompanyMembershipPermissionCatalog.TaskReportMargin, tokens);
    }

    [Fact]
    public void Mode_enum_carries_exactly_two_values()
    {
        var values = Enum.GetValues<TaskMarginReportMode>();
        Assert.Equal(2, values.Length);
        Assert.Contains(TaskMarginReportMode.Operational, values);
        Assert.Contains(TaskMarginReportMode.Billed, values);
    }

    [Fact]
    public void Query_defaults_to_take_200_and_skip_0()
    {
        var query = new TaskMarginReportQuery
        {
            CompanyId = CompanyId.FromOrdinal(1),
            BaseCurrencyCode = "USD",
            Mode = TaskMarginReportMode.Operational,
        };
        Assert.Equal(200, query.Take);
        Assert.Equal(0, query.Skip);
        Assert.Null(query.FromDate);
        Assert.Null(query.ToDate);
        Assert.Null(query.CustomerId);
        Assert.Null(query.AssignedToUserId);
        Assert.Equal("USD", query.BaseCurrencyCode);
    }

    [Fact]
    public void Row_margin_percent_is_null_when_billable_is_zero()
    {
        var row = new TaskMarginRow
        {
            TaskId = Guid.NewGuid(),
            TaskNo = "TSK-1",
            Title = "x",
            Status = TaskStatus.Open,
            CurrencyCode = "USD",
            BillableValue = 0m,
            DirectCost = 100m,
            GrossMargin = -100m,
            GrossMarginPercent = null,
            BaseCurrencyCode = "USD",
            FxRate = 1m,
            FxResolved = true,
            BillableValueBase = 0m,
            DirectCostBase = 100m,
            GrossMarginBase = -100m,
        };
        Assert.Null(row.GrossMarginPercent);
        Assert.Equal(-100m, row.GrossMargin);
        Assert.Equal(-100m, row.GrossMarginBase);
    }

    [Fact]
    public void Row_billable_base_tracks_invoice_fx_rate_cost_base_is_per_doc_locked()
    {
        // GL-aligned model: BillableValueBase = BillableValue × FxRate
        // (the linked invoice's posted rate for billed tasks, or
        // today's spot for unbilled). DirectCostBase is the sum of
        // each cost line × its parent bill/expense's own posted
        // fx_rate — so DirectCost × FxRate is NOT expected to equal
        // DirectCostBase in general (different rates on different
        // docs). UI reads DirectCostBase from the wire, never
        // recomputes it client-side.
        var row = new TaskMarginRow
        {
            TaskId = Guid.NewGuid(),
            TaskNo = "TSK-2",
            Title = "Cross-currency billed task",
            Status = TaskStatus.Billed,
            CurrencyCode = "USD",
            BilledInvoiceId = Guid.NewGuid(),
            BillableValue = 100m,
            DirectCost = 40m,        // sum of cost line amounts (mixed currencies, raw)
            GrossMargin = 60m,
            GrossMarginPercent = 60m,
            BaseCurrencyCode = "CAD",
            FxRate = 1.36m,          // invoice's posted rate
            FxResolved = true,
            BillableValueBase = 136m,    // 100 × 1.36
            DirectCostBase = 50m,        // each cost line at its own doc's posted rate; NOT 40 × 1.36
            GrossMarginBase = 86m,       // 136 − 50
        };
        Assert.True(row.FxResolved);
        Assert.Equal(1.36m, row.FxRate);
        Assert.Equal(136m, row.BillableValueBase);
        Assert.NotEqual(row.DirectCost * row.FxRate, row.DirectCostBase);
        Assert.Equal(row.BillableValueBase - row.DirectCostBase, row.GrossMarginBase);
    }

    [Fact]
    public void Summary_carries_base_currency_fields()
    {
        var summary = new TaskMarginSummary
        {
            TaskCount = 3,
            TotalBillableValue = 1000m,
            TotalDirectCost = 600m,
            TotalGrossMargin = 400m,
            WeightedGrossMarginPercent = 40m,
            BaseCurrencyCode = "CAD",
            TotalBillableValueBase = 1360m,
            TotalDirectCostBase = 816m,
            TotalGrossMarginBase = 544m,
            WeightedGrossMarginPercentBase = 40m,
            UnresolvedFxCount = 0,
        };
        Assert.Equal(40m, summary.WeightedGrossMarginPercent);
        Assert.Equal(400m, summary.TotalGrossMargin);
        Assert.Equal(544m, summary.TotalGrossMarginBase);
        Assert.Equal("CAD", summary.BaseCurrencyCode);
        Assert.Equal(0, summary.UnresolvedFxCount);
    }

    [Fact]
    public void Result_groups_rows_and_summary_under_a_single_mode()
    {
        var result = new TaskMarginReportResult
        {
            Mode = TaskMarginReportMode.Billed,
            Rows = Array.Empty<TaskMarginRow>(),
            Summary = new TaskMarginSummary
            {
                TaskCount = 0,
                TotalBillableValue = 0m,
                TotalDirectCost = 0m,
                TotalGrossMargin = 0m,
                WeightedGrossMarginPercent = null,
                BaseCurrencyCode = "USD",
                TotalBillableValueBase = 0m,
                TotalDirectCostBase = 0m,
                TotalGrossMarginBase = 0m,
                WeightedGrossMarginPercentBase = null,
                UnresolvedFxCount = 0,
            },
        };
        Assert.Equal(TaskMarginReportMode.Billed, result.Mode);
        Assert.Empty(result.Rows);
    }
}
