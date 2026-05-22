using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;
using Citus.Modules.Tasks.Application.Contracts;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// Mirror of <see cref="PostInvoiceCommandHandler"/> with two
/// surgical removals:
///   • No <c>IArOpenItemRepository</c> dependency. Sales receipts do
///     not open an AR row — the cash already arrived.
///   • No inventory shipment handoff gate. The shipment policy is
///     invoice-flow-specific (B2B fulfilment); cash sales settle on
///     the spot regardless of inventory state. (If a future feature
///     wants to gate sales-receipt posting on stock availability,
///     it lands as a separate gate, not the same one.)
///
/// H6-2b: when receipt lines carry task_id (and optionally
/// task_line_id), the post handler now flips the source tasks to
/// PartiallyBilled / Billed via <see cref="ITaskBillingCoordinator"/>
/// as a soft-failure post-step — same dual-path design as the
/// invoice handler (line-level when task_line_id is present, legacy
/// whole-task otherwise).
/// </summary>
public sealed class PostSalesReceiptCommandHandler
{
    private readonly ISalesReceiptDocumentRepository _documents;
    private readonly IPostingEngine _postingEngine;
    private readonly ITaskBillingCoordinator _taskBilling;
    private readonly IUnitOfWork _unitOfWork;

    public PostSalesReceiptCommandHandler(
        ISalesReceiptDocumentRepository documents,
        IPostingEngine postingEngine,
        ITaskBillingCoordinator taskBilling,
        IUnitOfWork unitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _taskBilling = taskBilling ?? throw new ArgumentNullException(nameof(taskBilling));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<PostSalesReceiptCommandResult> HandleAsync(
        PostSalesReceiptCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1 — JE post inside the UoW. Symmetric to the invoice
        // flow except for the AR + shipment surgical removals noted
        // on the class doc above.
        var postResult = await _unitOfWork.ExecuteAsync(async ct =>
        {
            var document = await _documents.GetForPostingAsync(command.CompanyId, command.DocumentId, ct);
            if (document is null)
            {
                throw new InvalidOperationException(
                    "Sales receipt document was not found in the active company context.");
            }

            var acceptedFxSnapshotId =
                command.AcceptedFxSnapshotId ??
                (document.FxSnapshot is { SnapshotId: var snapshotId } && snapshotId != Guid.Empty
                    ? snapshotId
                    : null);

            // Idempotency key shape mirrors the invoice flow. Default
            // is deterministic from (company, document) so a retry
            // with the same draft id collapses to one ledger write at
            // the JournalEntryWriter level — no double-post even
            // without an explicit operator-supplied key.
            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"sales_receipt:{command.CompanyId}:{command.DocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                acceptedFxSnapshotId,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);
            return PostSalesReceiptCommandResult.FromPostingResult(result);
        }, cancellationToken);

        // Step 2 — H6-2b: flip linked Task lines to billed. Soft-
        // failure: the receipt JE has already committed, so any error
        // surfaces in the outcome rather than rolling back the post.
        var taskBillingOutcome = await TryMarkLinkedTasksBilledAsync(command, cancellationToken);

        return postResult with { TaskBilling = taskBillingOutcome };
    }

    /// <summary>
    /// Step 2 hook. Returns null when the receipt has no task-linked
    /// lines (the typical cash-sale case). When non-null, the receipt
    /// JE has already posted — soft-failure is captured in
    /// <see cref="InvoiceTaskBillingOutcome.ErrorMessage"/>. The dual-
    /// path split (new line-level vs legacy whole-task) mirrors the
    /// invoice handler.
    /// </summary>
    private async Task<InvoiceTaskBillingOutcome?> TryMarkLinkedTasksBilledAsync(
        PostSalesReceiptCommand command,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SalesReceiptLineTaskLink> linkRows;
        try
        {
            linkRows = await _documents.ListLinkedTaskLineMappingsAsync(
                command.CompanyId,
                command.DocumentId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return new InvoiceTaskBillingOutcome(0, 0, ex.Message);
        }

        if (linkRows.Count == 0)
        {
            return null;
        }

        // Customer id off the document header for the cross-customer
        // guard. A read failure here would silently disable the guard,
        // so we surface a soft-failure outcome instead of fall through.
        Guid? customerId;
        try
        {
            var document = await _documents.GetForPostingAsync(
                command.CompanyId,
                command.DocumentId,
                cancellationToken);
            customerId = document?.CustomerId;
        }
        catch (Exception ex)
        {
            return new InvoiceTaskBillingOutcome(0, 0,
                "Customer lookup for cross-customer task-billing guard failed: " + ex.Message);
        }

        var lineLevelMappings = linkRows
            .Where(r => r.TaskLineId.HasValue)
            .Select(r => new TaskLineBillingMapping(r.TaskId, r.TaskLineId!.Value, r.SalesReceiptLineId))
            .ToArray();
        var tasksHandledByLinePath = lineLevelMappings
            .Select(m => m.TaskId)
            .ToHashSet();
        var legacyTaskIds = linkRows
            .Where(r => !r.TaskLineId.HasValue && !tasksHandledByLinePath.Contains(r.TaskId))
            .Select(r => r.TaskId)
            .Distinct()
            .ToArray();

        var processedCount = 0;
        var skippedCount = 0;

        if (lineLevelMappings.Length > 0)
        {
            try
            {
                var result = await _taskBilling.MarkLinesAsBilledAsync(
                    command.CompanyId,
                    sourceType: "sales_receipt",
                    sourceId: command.DocumentId,
                    customerId,
                    lineLevelMappings,
                    command.UserId,
                    cancellationToken);
                processedCount += result.ProcessedTasks.Count;
                skippedCount += result.SkippedTasks.Count;
            }
            catch (Exception ex)
            {
                return new InvoiceTaskBillingOutcome(processedCount, skippedCount, ex.Message);
            }
        }

        if (legacyTaskIds.Length > 0)
        {
            try
            {
                // Note: the legacy whole-task path uses the existing
                // MarkAsBilledAsync, which writes `billed_invoice_id`
                // on the task header — even for sales-receipt sources.
                // This is a known concession (the legacy column is
                // misnamed, but the data still encodes "what document
                // billed me" through TaskTransition rows for sales-
                // receipt receipts). The new line-level path above
                // does NOT stamp billed_invoice_id for sales-receipt
                // sources; it only writes the line-level audit trail.
                var result = await _taskBilling.MarkAsBilledAsync(
                    command.CompanyId,
                    command.DocumentId,
                    customerId,
                    legacyTaskIds,
                    command.UserId,
                    cancellationToken);
                processedCount += result.ProcessedTasks.Count;
                skippedCount += result.SkippedTasks.Count;
            }
            catch (Exception ex)
            {
                return new InvoiceTaskBillingOutcome(processedCount, skippedCount, ex.Message);
            }
        }

        return new InvoiceTaskBillingOutcome(processedCount, skippedCount, ErrorMessage: null);
    }
}
