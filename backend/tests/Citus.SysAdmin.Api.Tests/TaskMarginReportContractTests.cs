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
            Mode = TaskMarginReportMode.Operational,
        };
        Assert.Equal(200, query.Take);
        Assert.Equal(0, query.Skip);
        Assert.Null(query.FromDate);
        Assert.Null(query.ToDate);
        Assert.Null(query.CustomerId);
        Assert.Null(query.AssignedToUserId);
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
        };
        Assert.Null(row.GrossMarginPercent);
        Assert.Equal(-100m, row.GrossMargin);
    }

    [Fact]
    public void Summary_carries_weighted_margin_percent_field()
    {
        var summary = new TaskMarginSummary
        {
            TaskCount = 3,
            TotalBillableValue = 1000m,
            TotalDirectCost = 600m,
            TotalGrossMargin = 400m,
            WeightedGrossMarginPercent = 40m,
        };
        Assert.Equal(40m, summary.WeightedGrossMarginPercent);
        Assert.Equal(400m, summary.TotalGrossMargin);
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
            },
        };
        Assert.Equal(TaskMarginReportMode.Billed, result.Mode);
        Assert.Empty(result.Rows);
    }
}
