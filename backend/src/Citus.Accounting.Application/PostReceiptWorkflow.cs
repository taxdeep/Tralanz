using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Application;

public sealed class PostReceiptWorkflow
{
    private readonly IReceiptDocumentRepository _documents;
    private readonly IReceiptInventoryActivationStore _activationStore;

    public PostReceiptWorkflow(
        IReceiptDocumentRepository documents,
        IReceiptInventoryActivationStore activationStore)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _activationStore = activationStore ?? throw new ArgumentNullException(nameof(activationStore));
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
            await _activationStore.ValidateCanActivateAsync(companyId.Value, documentId, cancellationToken);

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

        try
        {
            await _activationStore.ActivatePostedReceiptAsync(
                companyId.Value,
                userId.Value,
                documentId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            await _activationStore.RecordActivationFailureAsync(
                companyId.Value,
                userId.Value,
                documentId,
                ex.Message,
                cancellationToken);
            throw;
        }

        return result;
    }
}
