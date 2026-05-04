using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Application.Commands;

public sealed class PostInvoiceCommandHandler
{
    private readonly IInvoiceDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly IArOpenItemRepository _openItems;
    private readonly IInventoryShipmentStore _inventoryShipmentStore;
    private readonly PostSalesIssueCogsCommandHandler _cogsHandler;
    private readonly IUnitOfWork _unitOfWork;

    public PostInvoiceCommandHandler(
        IInvoiceDocumentRepository documents,
        IPostingEngine postingEngine,
        IArOpenItemRepository openItems,
        IInventoryShipmentStore inventoryShipmentStore,
        PostSalesIssueCogsCommandHandler cogsHandler,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _openItems = openItems ?? throw new ArgumentNullException(nameof(openItems));
        _inventoryShipmentStore = inventoryShipmentStore ?? throw new ArgumentNullException(nameof(inventoryShipmentStore));
        _cogsHandler = cogsHandler ?? throw new ArgumentNullException(nameof(cogsHandler));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<PostInvoiceCommandResult> HandleAsync(
        PostInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1 — invoice JE + AR open item, transactional. If anything in
        // here throws, the invoice is NOT posted and the caller gets a
        // BadRequest. (Auto-COGS does not run.)
        var invoicePostResult = await _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException("Invoice document was not found in the active company context.");
            }

            var shipmentHandoffSummary = await _inventoryShipmentStore.GetInvoiceHandoffSummaryAsync(
                command.CompanyId.Value,
                command.DocumentId,
                ct);
            if (!ShipmentPostingGatePolicy.AllowsInvoicePost(shipmentHandoffSummary.MatchStatus))
            {
                throw new InvalidOperationException(
                    ShipmentPostingGatePolicy.GetBlockedPostMessage(shipmentHandoffSummary));
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"invoice:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                acceptedFxSnapshotId,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            var originalAmountBase = Math.Round(
                document.TotalAmount * (document.FxSnapshot?.Rate ?? 1m),
                2,
                MidpointRounding.ToEven);

            await _openItems.EnsureForInvoiceAsync(document, originalAmountBase, ct);
            return PostInvoiceCommandResult.FromPostingResult(result);
        }, cancellationToken);

        // Step 2 — auto-trigger COGS post for every linked posted sales-issue.
        // Runs OUTSIDE the invoice unit of work so a COGS failure cannot roll
        // back the invoice (matching principle: revenue is recognised; missing
        // COGS is a configuration error the operator can resolve via the
        // workbench at /company/inventory/cogs-postings).
        var cogsOutcomes = await TryAutoPostCogsAsync(command, cancellationToken);

        return invoicePostResult with { AutoPostedCogs = cogsOutcomes };
    }

    /// <summary>
    /// Discovers sales-issues linked to this invoice via the existing
    /// shipment-issue lane summary (no new query path needed) and calls
    /// <see cref="PostSalesIssueCogsCommandHandler"/> for each posted one.
    /// Per-issue exceptions are logged and surfaced in the outcome list;
    /// they never throw out of this method.
    /// </summary>
    private async Task<IReadOnlyList<InvoiceAutoCogsOutcome>> TryAutoPostCogsAsync(
        PostInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        InventoryInvoiceShipmentIssueLaneSummary lane;
        try
        {
            lane = await _inventoryShipmentStore.GetInvoiceLaneSummaryAsync(
                command.CompanyId.Value,
                command.DocumentId,
                cancellationToken);
        }
        catch
        {
            // Lane summary unavailable (inventory module not enabled, race
            // condition, etc.): silently skip auto-COGS. The workbench at
            // /company/inventory/cogs-postings remains as the recovery path
            // and surfaces any sales-issues that need posting manually.
            return Array.Empty<InvoiceAutoCogsOutcome>();
        }

        if (lane.RecentIssues.Count == 0)
        {
            return Array.Empty<InvoiceAutoCogsOutcome>();
        }

        var outcomes = new List<InvoiceAutoCogsOutcome>(lane.RecentIssues.Count);
        foreach (var issue in lane.RecentIssues)
        {
            // Only try posted issues. The invoice posting gate already requires
            // the shipment side to be complete, so non-posted issues here are
            // the rare race-condition or partial-post case — skip silently and
            // let the workbench pick them up.
            if (!string.Equals(issue.Status, "posted", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var result = await _cogsHandler.HandleAsync(
                    new PostSalesIssueCogsCommand(command.CompanyId, command.UserId, issue.DocumentId),
                    cancellationToken);

                outcomes.Add(new InvoiceAutoCogsOutcome(
                    SalesIssueDocumentId: issue.DocumentId,
                    JournalEntryId: result.JournalEntryId,
                    JournalEntryDisplayNumber: result.JournalEntryDisplayNumber,
                    AlreadyPosted: result.AlreadyPosted,
                    Succeeded: true,
                    ErrorMessage: null));
            }
            catch (Exception ex)
            {
                // Soft failure: record the outcome but keep going. Operator
                // sees the failed sales-issue id + reason in the toast and
                // can replay it from the COGS workbench.
                outcomes.Add(new InvoiceAutoCogsOutcome(
                    SalesIssueDocumentId: issue.DocumentId,
                    JournalEntryId: null,
                    JournalEntryDisplayNumber: null,
                    AlreadyPosted: false,
                    Succeeded: false,
                    ErrorMessage: ex.Message));
            }
        }

        return outcomes;
    }
}
