using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Application;

public sealed class PostReceiptWorkflow
{
    private readonly IReceiptDocumentRepository _documents;
    private readonly IReceiptInventoryActivationStore _activationStore;
    private readonly IReceiptInventoryValuationStore _valuationStore;
    private readonly IReceiptInventoryCostLayerEmissionStore _emissionStore;
    private readonly IInventoryReceiptUnitOfWork _inventoryUnitOfWork;

    public PostReceiptWorkflow(
        IReceiptDocumentRepository documents,
        IReceiptInventoryActivationStore activationStore,
        IReceiptInventoryValuationStore valuationStore,
        IReceiptInventoryCostLayerEmissionStore emissionStore,
        IInventoryReceiptUnitOfWork inventoryUnitOfWork)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _activationStore = activationStore ?? throw new ArgumentNullException(nameof(activationStore));
        _valuationStore = valuationStore ?? throw new ArgumentNullException(nameof(valuationStore));
        _emissionStore = emissionStore ?? throw new ArgumentNullException(nameof(emissionStore));
        _inventoryUnitOfWork = inventoryUnitOfWork ?? throw new ArgumentNullException(nameof(inventoryUnitOfWork));
    }

    public async Task<SourceDocumentDraftSaveResult> PostAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(documentId));
        }

        var document = await _documents.GetAsync(companyId, documentId, cancellationToken);
        if (document is null)
        {
            throw new InvalidOperationException("Receipt document was not found in the active company context.");
        }

        SourceDocumentDraftSaveResult result;

        if (string.Equals(document.Status, ReceiptDocumentStatuses.Draft, StringComparison.OrdinalIgnoreCase))
        {
            await _activationStore.ValidateCanActivateAsync(companyId, documentId, cancellationToken);

            result = await _documents.PostAsync(
                companyId,
                userId,
                documentId,
                cancellationToken);
        }
        else if (string.Equals(document.Status, ReceiptDocumentStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            result = new SourceDocumentDraftSaveResult(
                document.Id,
                document.EntityNumber.Value,
                document.DisplayNumber.Value,
                ReceiptDocumentStatuses.Posted);
        }
        else
        {
            throw new InvalidOperationException("Only draft or already-posted receipts can enter inventory activation.");
        }

        // M4 (AUDIT_2026-05-20 P2-10): wrap activation + valuation +
        // emission in one Npgsql transaction via the inventory UoW.
        // Each store joins the ambient tx through the
        // InventoryReceiptExecutionContextAccessor; failure at any
        // step rolls back every prior step (including activation
        // rows) so the receipt never lands in an "activated but
        // un-emitted" state where a later sales-issue would consume
        // stale cost layers and produce wrong COGS.
        //
        // Step 1 (document.PostAsync) intentionally stays OUTSIDE
        // this tx: the document's posted state + entity-number
        // allocation is a separate boundary, and the retry endpoint
        // resumes from the same posted state if the inventory cycle
        // is interrupted.
        try
        {
            await _inventoryUnitOfWork.ExecuteAsync(async ct =>
            {
                await _activationStore.ActivatePostedReceiptAsync(
                    companyId,
                    userId,
                    documentId,
                    ct);

                await _valuationStore.RefreshReceiptValuationAsync(
                    companyId,
                    userId,
                    documentId,
                    ct);

                await _emissionStore.EmitReceiptCostLayersAsync(
                    companyId,
                    userId,
                    documentId,
                    ct);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            // The UoW already rolled back the inventory tx. Record
            // the failure on a separate (post-rollback) tx so the
            // retry workbench can show what went wrong. The original
            // catch only handled activation failures; the new
            // wrapping also catches valuation + emission failures,
            // which is the intent — a failure at any step is an
            // "inventory cycle failure" that the operator needs to
            // see.
            await _activationStore.RecordActivationFailureAsync(
                companyId,
                userId,
                documentId,
                ex.Message,
                cancellationToken);
            throw;
        }

        return result;
    }
}
