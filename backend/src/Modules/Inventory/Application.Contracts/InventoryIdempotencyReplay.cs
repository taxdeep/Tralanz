namespace Citus.Modules.Inventory.Application.Contracts;

/// <summary>
/// Thrown by an inventory POST store when a request arrives with an
/// <c>Idempotency-Key</c> that matches an already-posted document in
/// the same company. Callers (endpoints) catch this and surface the
/// existing document id instead of re-running the side-effecting
/// writes (cost layers, stock balances, JE postings, etc.).
///
/// The "key matches" detection is enforced by the partial unique
/// index <c>ux_inventory_documents_idempotency</c> in
/// PostgreSQL — the store catches the resulting 23505 violation and
/// translates it to this typed exception so handlers don't need
/// to grep Postgres error codes themselves.
/// </summary>
public sealed class InventoryIdempotencyReplayException : Exception
{
    public InventoryIdempotencyReplayException(
        Guid existingDocumentId,
        string existingDocumentNumber,
        string documentType,
        string idempotencyKey)
        : base(
            $"Inventory {documentType} with Idempotency-Key '{idempotencyKey}' " +
            $"was already posted as {existingDocumentNumber} ({existingDocumentId:D}).")
    {
        ExistingDocumentId = existingDocumentId;
        ExistingDocumentNumber = existingDocumentNumber;
        DocumentType = documentType;
        IdempotencyKey = idempotencyKey;
    }

    public Guid ExistingDocumentId { get; }

    public string ExistingDocumentNumber { get; }

    public string DocumentType { get; }

    public string IdempotencyKey { get; }
}
