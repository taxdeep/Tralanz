using Citus.Modules.Inventory.Application;

namespace Citus.Accounting.Api.Tests;

public sealed class InventoryShipmentMatchingPolicyTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, "no_inventory_handoff")]
    [InlineData(1, 0, 10, 0, "no_shipment")]
    [InlineData(1, 1, 10, 4, "partially_shipped")]
    [InlineData(1, 1, 10, 10, "fully_shipped")]
    [InlineData(1, 1, 10, 12, "over_shipped")]
    public void ResolveInvoiceShipmentDocumentStatus_ReturnsExpectedValue(
        int invoiceLineCount,
        int shipmentCount,
        decimal invoiceQuantity,
        decimal shippedQuantity,
        string expected)
    {
        var actual = InventoryShipmentMatchingWorkflow.ResolveInvoiceShipmentDocumentStatus(
            invoiceLineCount,
            shipmentCount,
            invoiceQuantity,
            shippedQuantity);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "no_shipment")]
    [InlineData(1, 0, 10, 0, "pending_issue")]
    [InlineData(1, 1, 10, 3, "partially_issued")]
    [InlineData(1, 1, 10, 10, "fully_issued")]
    [InlineData(1, 1, 10, 11, "over_issued")]
    public void ResolveShipmentIssueDocumentStatus_ReturnsExpectedValue(
        int shipmentCount,
        int issueCount,
        decimal shippedQuantity,
        decimal issuedQuantity,
        string expected)
    {
        var actual = InventoryShipmentMatchingWorkflow.ResolveShipmentIssueDocumentStatus(
            shipmentCount,
            issueCount,
            shippedQuantity,
            issuedQuantity);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveShipmentIssueLineStatus_SupportsSplitFulfillmentWithoutPretendingFullIssue()
    {
        var actual = InventoryShipmentMatchingWorkflow.ResolveShipmentIssueLineStatus(
            7.5m,
            4.25m);

        Assert.Equal("partially_issued", actual);
    }

    [Theory]
    [InlineData("over_shipped", true)]
    [InlineData("over_issued", true)]
    [InlineData("partially_shipped", false)]
    [InlineData("fully_issued", false)]
    public void IsDiscrepancyStatus_OnlyFlagsTrueMismatchStates(string status, bool expected)
    {
        Assert.Equal(expected, InventoryShipmentMatchingWorkflow.IsDiscrepancyStatus(status));
    }
}
