using Citus.Accounting.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class ShipmentIssuePostingGatePolicyTests
{
    [Theory]
    [InlineData("no_inventory_handoff", true)]
    [InlineData("fully_issued", true)]
    [InlineData("no_issue", false)]
    [InlineData("partially_issued", false)]
    [InlineData("over_issued", false)]
    [InlineData("unexpected", false)]
    public void AllowsInvoicePost_MatchesIssueFirstAuthority(string matchStatus, bool expected)
    {
        Assert.Equal(expected, ShipmentIssuePostingGatePolicy.AllowsInvoicePost(matchStatus));
    }

    [Fact]
    public void GetBlockedPostMessage_UsesRemainingQuantity_ForPartialIssue()
    {
        var summary = new InventoryInvoiceIssueHandoffSummary(
            Guid.NewGuid(),
            1,
            12m,
            1,
            4.5m,
            7.5m,
            "partially_issued",
            null,
            Array.Empty<InventorySalesIssueSummary>(),
            Array.Empty<InventoryInvoiceIssueHandoffLineSummary>());

        var message = ShipmentIssuePostingGatePolicy.GetBlockedPostMessage(summary);

        Assert.Contains("7.50", message);
        Assert.Contains("outstanding", message);
    }

    [Theory]
    [InlineData("no_issue", "Post on hold: no sales issue yet")]
    [InlineData("partially_issued", "Post on hold: issue still partial")]
    [InlineData("over_issued", "Post on hold: issue mismatch")]
    [InlineData("fully_issued", "Post enabled")]
    [InlineData("no_inventory_handoff", "Post enabled")]
    public void GetPostingGateLabel_ReturnsExpectedOperatorLabel(string matchStatus, string expected)
    {
        var summary = CreateSummary(matchStatus);

        Assert.Equal(expected, ShipmentIssuePostingGatePolicy.GetPostingGateLabel(summary));
    }

    private static InventoryInvoiceIssueHandoffSummary CreateSummary(string matchStatus) =>
        new(
            Guid.NewGuid(),
            1,
            10m,
            1,
            matchStatus == "partially_issued" ? 7.75m : 10m,
            matchStatus == "partially_issued" ? 2.25m : 0m,
            matchStatus,
            null,
            Array.Empty<InventorySalesIssueSummary>(),
            Array.Empty<InventoryInvoiceIssueHandoffLineSummary>());
}
