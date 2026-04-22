namespace Citus.Modules.Inventory.Application.Contracts;

public static class ReceiptGrIrBridgeStatusPolicy
{
    public const string NotEligible = "not_eligible";
    public const string EligibleNotPosted = "eligible_not_posted";
    public const string PartiallyPosted = "partially_posted";
    public const string Posted = "posted";
    public const string BlockedReconciliationRequired = "blocked_reconciliation_required";
    public const string BlockedVarianceRequired = "blocked_variance_required";

    public static string Resolve(
        int bridgeLineCount,
        int eligibleLineCount,
        int blockedReconciliationLineCount,
        int blockedVarianceLineCount,
        int postedLineCount,
        int partiallyPostedLineCount)
    {
        if (bridgeLineCount <= 0)
        {
            return NotEligible;
        }

        if (blockedReconciliationLineCount > 0)
        {
            return BlockedReconciliationRequired;
        }

        if (blockedVarianceLineCount > 0)
        {
            return BlockedVarianceRequired;
        }

        if (postedLineCount >= bridgeLineCount)
        {
            return Posted;
        }

        if (postedLineCount > 0 || partiallyPostedLineCount > 0)
        {
            return PartiallyPosted;
        }

        if (eligibleLineCount > 0)
        {
            return EligibleNotPosted;
        }

        return NotEligible;
    }
}
