namespace Citus.Modules.Inventory.Application;

public static class InventoryOutboundStateWorkflow
{
    public static decimal ResolveInvoicedQuantity(string? invoiceStatus, decimal invoiceQuantity) =>
        string.Equals(invoiceStatus, "posted", StringComparison.OrdinalIgnoreCase)
            ? invoiceQuantity
            : 0m;

    public static string ResolveInvoiceCoverageStatus(
        int invoiceOutboundLineCount,
        string? invoiceStatus,
        decimal shippedQuantity,
        decimal invoiceQuantity)
    {
        if (invoiceOutboundLineCount <= 0)
        {
            return "no_inventory_handoff";
        }

        if (shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        var invoicedQuantity = ResolveInvoicedQuantity(invoiceStatus, invoiceQuantity);
        if (invoicedQuantity <= 0m)
        {
            return "not_invoiced";
        }

        return ResolveCoverageStatus(
            shippedQuantity,
            invoicedQuantity,
            "partially_invoiced",
            "fully_invoiced",
            "over_invoiced");
    }

    public static string ResolveInvoiceCoverageLineStatus(
        string? invoiceStatus,
        decimal shippedQuantity,
        decimal invoiceQuantity)
    {
        if (shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        var invoicedQuantity = ResolveInvoicedQuantity(invoiceStatus, invoiceQuantity);
        if (invoicedQuantity <= 0m)
        {
            return "not_invoiced";
        }

        return ResolveCoverageStatus(
            shippedQuantity,
            invoicedQuantity,
            "partially_invoiced",
            "fully_invoiced",
            "over_invoiced");
    }

    public static decimal ResolveRemainingToInvoiceQuantity(
        string? invoiceStatus,
        decimal shippedQuantity,
        decimal invoiceQuantity)
    {
        var invoicedQuantity = ResolveInvoicedQuantity(invoiceStatus, invoiceQuantity);
        return decimal.Round(
            Math.Max(0m, shippedQuantity - invoicedQuantity),
            6,
            MidpointRounding.AwayFromZero);
    }

    public static bool IsDiscrepancyStatus(string? status) =>
        string.Equals(status, "over_shipped", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "over_issued", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "over_invoiced", StringComparison.OrdinalIgnoreCase);

    private static string ResolveCoverageStatus(
        decimal sourceQuantity,
        decimal matchedQuantity,
        string partialStatus,
        string fullStatus,
        string overStatus)
    {
        if (matchedQuantity < sourceQuantity)
        {
            return partialStatus;
        }

        if (matchedQuantity == sourceQuantity)
        {
            return fullStatus;
        }

        return overStatus;
    }
}
