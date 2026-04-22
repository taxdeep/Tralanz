using Citus.Accounting.Application;
using Citus.Accounting.Application.Repositories;

public static class BillReceiptPostingGate
{
    public static bool AllowsBillPost(string? matchStatus) =>
        BillReceiptPostingGatePolicy.AllowsBillPost(matchStatus);

    public static string GetPostingGateLabel(BillReceiptMatchingLaneSummary? summary) =>
        BillReceiptPostingGatePolicy.GetPostingGateLabel(summary);

    public static string GetPostingGateLabel(BillReceiptPostingGateSnapshot? snapshot) =>
        BillReceiptPostingGatePolicy.GetPostingGateLabel(snapshot);

    public static string GetPostingGateSummary(BillReceiptMatchingLaneSummary? summary) =>
        BillReceiptPostingGatePolicy.GetPostingGateSummary(summary);

    public static string GetPostingGateSummary(BillReceiptPostingGateSnapshot? snapshot) =>
        BillReceiptPostingGatePolicy.GetPostingGateSummary(snapshot);

    public static string GetBlockedPostMessage(BillReceiptMatchingLaneSummary summary) =>
        BillReceiptPostingGatePolicy.GetBlockedPostMessage(summary);
}
