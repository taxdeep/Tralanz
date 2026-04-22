namespace Citus.Modules.Inventory.Application;

public static class InventoryShipmentMatchingWorkflow
{
    public static string ResolveInvoiceShipmentDocumentStatus(
        int invoiceOutboundLineCount,
        int shipmentCount,
        decimal invoiceQuantity,
        decimal shippedQuantity)
    {
        if (invoiceOutboundLineCount <= 0)
        {
            return "no_inventory_handoff";
        }

        return ResolveCoverageStatus(
            shipmentCount,
            invoiceQuantity,
            shippedQuantity,
            "no_shipment",
            "partially_shipped",
            "fully_shipped",
            "over_shipped");
    }

    public static string ResolveInvoiceShipmentLineStatus(
        decimal invoiceQuantity,
        decimal shippedQuantity) =>
        ResolveCoverageStatus(
            shippedQuantity > 0m ? 1 : 0,
            invoiceQuantity,
            shippedQuantity,
            "no_shipment",
            "partially_shipped",
            "fully_shipped",
            "over_shipped");

    public static string ResolveShipmentIssueDocumentStatus(
        int shipmentCount,
        int issueCount,
        decimal shippedQuantity,
        decimal issuedQuantity)
    {
        if (shipmentCount <= 0 || shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        return ResolveCoverageStatus(
            issueCount,
            shippedQuantity,
            issuedQuantity,
            "pending_issue",
            "partially_issued",
            "fully_issued",
            "over_issued");
    }

    public static string ResolveShipmentIssueLineStatus(
        decimal shippedQuantity,
        decimal issuedQuantity)
    {
        if (shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        return ResolveCoverageStatus(
            issuedQuantity > 0m ? 1 : 0,
            shippedQuantity,
            issuedQuantity,
            "pending_issue",
            "partially_issued",
            "fully_issued",
            "over_issued");
    }

    public static bool IsDiscrepancyStatus(string status) =>
        string.Equals(status, "over_shipped", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "over_issued", StringComparison.OrdinalIgnoreCase);

    private static string ResolveCoverageStatus(
        int matchedDocumentCount,
        decimal sourceQuantity,
        decimal matchedQuantity,
        string noneStatus,
        string partialStatus,
        string fullStatus,
        string overStatus)
    {
        var roundedSource = decimal.Round(sourceQuantity, 6, MidpointRounding.AwayFromZero);
        var roundedMatched = decimal.Round(matchedQuantity, 6, MidpointRounding.AwayFromZero);
        var remaining = decimal.Round(roundedSource - roundedMatched, 6, MidpointRounding.AwayFromZero);

        if (matchedDocumentCount <= 0 || roundedMatched <= 0m)
        {
            return noneStatus;
        }

        if (remaining > 0m)
        {
            return partialStatus;
        }

        if (remaining == 0m)
        {
            return fullStatus;
        }

        return overStatus;
    }
}
