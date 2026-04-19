namespace Citus.Modules.Inventory.Application;

public static class InventoryReturnReceivePolicy
{
    public static decimal ResolveRemainingReturnableQuantity(
        decimal shippedQuantity,
        decimal returnedQuantity) =>
        decimal.Round(shippedQuantity - returnedQuantity, 6, MidpointRounding.AwayFromZero);

    public static string ResolveMatchStatus(
        int shipmentLineCount,
        decimal shippedQuantity,
        decimal returnedQuantity)
    {
        if (shipmentLineCount == 0)
        {
            return "no_shipment";
        }

        if (returnedQuantity <= 0m)
        {
            return "not_returned";
        }

        return ResolveCoverageStatus(
            shippedQuantity,
            returnedQuantity,
            "partially_returned",
            "fully_returned",
            "over_returned");
    }

    public static string ResolveLineMatchStatus(
        decimal shippedQuantity,
        decimal returnedQuantity)
    {
        if (shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        if (returnedQuantity <= 0m)
        {
            return "not_returned";
        }

        return ResolveCoverageStatus(
            shippedQuantity,
            returnedQuantity,
            "partially_returned",
            "fully_returned",
            "over_returned");
    }

    private static string ResolveCoverageStatus(
        decimal sourceQuantity,
        decimal matchedQuantity,
        string partialStatus,
        string fullStatus,
        string overStatus)
    {
        var delta = decimal.Round(matchedQuantity - sourceQuantity, 6, MidpointRounding.AwayFromZero);
        if (delta < 0m)
        {
            return partialStatus;
        }

        if (delta == 0m)
        {
            return fullStatus;
        }

        return overStatus;
    }
}
