using Citus.Modules.Inventory.Application.Contracts;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

/// <summary>
/// Small shared helper that translates the Postgres 23505 unique-
/// violation on <c>ux_inventory_documents_idempotency</c> into a
/// typed <see cref="InventoryIdempotencyReplayException"/>. Used by
/// the Receipt / Issue / Shipment / Transfer stores so they all
/// surface the same replay shape to endpoints.
/// </summary>
public static class InventoryIdempotencyHelper
{
    /// <summary>
    /// Postgres unique-violation SQL state. Use as a constant rather
    /// than a magic string so the catch sites read clearly.
    /// </summary>
    public const string UniqueViolationSqlState = "23505";

    /// <summary>
    /// Partial unique index name from the
    /// 2026-05-20-inventory-idempotency-key migration. Used to
    /// distinguish "duplicate idempotency-key" from other unique
    /// violations a store might hit (e.g. document_number race).
    /// </summary>
    public const string IdempotencyIndexName = "ux_inventory_documents_idempotency";

    /// <summary>
    /// Returns true iff the <paramref name="ex"/> originated from
    /// the idempotency-key unique violation specifically (not some
    /// other unique constraint on inventory_documents).
    /// </summary>
    public static bool IsIdempotencyViolation(PostgresException ex) =>
        ex.SqlState == UniqueViolationSqlState
        && (ex.ConstraintName == IdempotencyIndexName
            || ex.Message.Contains(IdempotencyIndexName, StringComparison.Ordinal));

    /// <summary>
    /// Reads the (id, document_number, document_type) of the existing
    /// row that owns the idempotency key and throws a typed replay
    /// exception. Always throws — never returns. Opens its own short-
    /// lived connection so callers can invoke this from the catch arm
    /// of an INSERT whose transaction was already rolled back.
    /// </summary>
    public static async Task ThrowReplayAsync(
        PostgreSqlConnectionFactory connections,
        CompanyId companyId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, document_number, document_type
              from inventory_documents
             where company_id = @company_id
               and idempotency_key = @key
             limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // Race we can't explain — the unique violation fired but
            // no row matches the key. Surface as a regular error so
            // ops can investigate; do NOT throw the replay exception
            // because we can't honestly say "this was already posted".
            throw new InvalidOperationException(
                $"Inventory idempotency violation for company {companyId} key '{idempotencyKey}' " +
                "but the conflicting row could not be resolved. Manual investigation required.");
        }
        throw new InventoryIdempotencyReplayException(
            existingDocumentId: reader.GetGuid(0),
            existingDocumentNumber: reader.GetString(1),
            documentType: reader.GetString(2),
            idempotencyKey: idempotencyKey);
    }
}
