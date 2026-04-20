namespace Citus.Modules.Inventory.Application.Contracts;

public static class ReceiptInventoryActivationStatusPolicy
{
    public const string NotPosted = "not_posted";
    public const string PostedNotActivated = "posted_not_activated";
    public const string Activated = "activated";
    public const string ActivationFailedRetryable = "activation_failed_retryable";
    public const string ActivationInconsistent = "activation_inconsistent";
    public const string Missing = "missing";

    public static string Resolve(
        string receiptStatus,
        int receiptLineCount,
        int activatedLineCount,
        bool hasRetryableFailure)
    {
        if (string.IsNullOrWhiteSpace(receiptStatus) ||
            string.Equals(receiptStatus, Missing, StringComparison.OrdinalIgnoreCase))
        {
            return Missing;
        }

        if (!string.Equals(receiptStatus, "posted", StringComparison.OrdinalIgnoreCase))
        {
            return NotPosted;
        }

        if (activatedLineCount == receiptLineCount && receiptLineCount > 0)
        {
            return Activated;
        }

        if (activatedLineCount > 0)
        {
            return ActivationInconsistent;
        }

        return hasRetryableFailure
            ? ActivationFailedRetryable
            : PostedNotActivated;
    }
}
