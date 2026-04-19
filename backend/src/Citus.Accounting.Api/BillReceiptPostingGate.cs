using Citus.Accounting.Application;
using Citus.Modules.Inventory.Application.Contracts;

public static class BillReceiptPostingGate
{
    public static bool AllowsBillPost(string? matchStatus) =>
        BillReceiptPostingGatePolicy.AllowsBillPost(matchStatus);

    public static string GetPostingGateLabel(InventoryBillReceiptHandoffSummary? summary) =>
        BillReceiptPostingGatePolicy.GetPostingGateLabel(summary);

    public static string GetPostingGateLabel(InventoryBillReceiptPostingGateSnapshot? snapshot) =>
        BillReceiptPostingGatePolicy.GetPostingGateLabel(snapshot);

    public static string GetPostingGateSummary(InventoryBillReceiptHandoffSummary? summary) =>
        BillReceiptPostingGatePolicy.GetPostingGateSummary(summary);

    public static string GetPostingGateSummary(InventoryBillReceiptPostingGateSnapshot? snapshot) =>
        BillReceiptPostingGatePolicy.GetPostingGateSummary(snapshot);

    public static string GetBlockedPostMessage(InventoryBillReceiptHandoffSummary summary) =>
        BillReceiptPostingGatePolicy.GetBlockedPostMessage(summary);
}
