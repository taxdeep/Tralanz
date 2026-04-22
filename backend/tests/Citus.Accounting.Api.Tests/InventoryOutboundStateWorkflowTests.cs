using Citus.Modules.Inventory.Application;

namespace Citus.Accounting.Api.Tests;

public sealed class InventoryOutboundStateWorkflowTests
{
    [Theory]
    [InlineData(0, "draft", 0, 0, "no_inventory_handoff")]
    [InlineData(1, "draft", 0, 10, "no_shipment")]
    [InlineData(1, "draft", 4, 10, "not_invoiced")]
    [InlineData(1, "posted", 4, 10, "over_invoiced")]
    [InlineData(1, "posted", 10, 10, "fully_invoiced")]
    [InlineData(1, "posted", 12, 10, "partially_invoiced")]
    public void ResolveInvoiceCoverageStatus_returns_expected_status(
        int outboundLineCount,
        string invoiceStatus,
        decimal shippedQuantity,
        decimal invoiceQuantity,
        string expected)
    {
        var status = InventoryOutboundStateWorkflow.ResolveInvoiceCoverageStatus(
            outboundLineCount,
            invoiceStatus,
            shippedQuantity,
            invoiceQuantity);

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("draft", 0, 10, "no_shipment")]
    [InlineData("draft", 6, 10, "not_invoiced")]
    [InlineData("posted", 10, 10, "fully_invoiced")]
    [InlineData("posted", 12, 10, "partially_invoiced")]
    [InlineData("posted", 8, 10, "over_invoiced")]
    public void ResolveInvoiceCoverageLineStatus_returns_expected_status(
        string invoiceStatus,
        decimal shippedQuantity,
        decimal invoiceQuantity,
        string expected)
    {
        var status = InventoryOutboundStateWorkflow.ResolveInvoiceCoverageLineStatus(
            invoiceStatus,
            shippedQuantity,
            invoiceQuantity);

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("draft", 0, 10, 0)]
    [InlineData("draft", 6, 10, 6)]
    [InlineData("posted", 10, 10, 0)]
    [InlineData("posted", 12, 10, 2)]
    [InlineData("posted", 8, 10, 0)]
    public void ResolveRemainingToInvoiceQuantity_returns_expected_quantity(
        string invoiceStatus,
        decimal shippedQuantity,
        decimal invoiceQuantity,
        decimal expected)
    {
        var quantity = InventoryOutboundStateWorkflow.ResolveRemainingToInvoiceQuantity(
            invoiceStatus,
            shippedQuantity,
            invoiceQuantity);

        Assert.Equal(expected, quantity);
    }

    [Theory]
    [InlineData("over_shipped", true)]
    [InlineData("over_issued", true)]
    [InlineData("over_invoiced", true)]
    [InlineData("fully_invoiced", false)]
    public void IsDiscrepancyStatus_matches_expected_value(string status, bool expected)
    {
        Assert.Equal(expected, InventoryOutboundStateWorkflow.IsDiscrepancyStatus(status));
    }
}
