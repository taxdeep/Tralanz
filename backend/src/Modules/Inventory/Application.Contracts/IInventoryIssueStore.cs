namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryIssueStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<InventorySalesIssueDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<InventoryInvoiceIssueHandoffSummary> GetInvoiceHandoffSummaryAsync(
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, InventoryInvoiceIssuePostingGateSnapshot>> GetInvoicePostingGateSnapshotsAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> invoiceDocumentIds,
        CancellationToken cancellationToken);

    Task<InventorySalesIssueSummary> PostAsync(
        InventorySalesIssuePostRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// P0-2 (C2): undoes the outbound subledger effects of a posted
    /// sales-issue as part of an invoice-reverse run. Restores
    /// <c>inventory_cost_layers.remaining_qty</c> + <c>remaining_cost_base</c>,
    /// re-increments <c>item_warehouse_balances.on_hand_qty</c>, and writes
    /// a compensating inbound row to <c>inventory_ledger_entries</c> per
    /// original line. Idempotent via <c>inventory_documents.reversed_at</c>
    /// — a re-run after a successful reverse returns
    /// <see cref="InventorySalesIssueReverseSummary.AlreadyReversed"/>=true.
    ///
    /// The compensating GL JE (Dr Inventory / Cr COGS) is posted by
    /// <c>PostSalesIssueCogsReverseCommandHandler</c> separately so the
    /// inventory subledger and ledger sides stay independently
    /// idempotent.
    /// </summary>
    Task<InventorySalesIssueReverseSummary> ReverseForInvoiceAsync(
        CompanyId companyId,
        Guid salesIssueDocumentId,
        Guid invoiceId,
        UserId actorId,
        CancellationToken cancellationToken);
}
