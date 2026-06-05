using Npgsql;

namespace SharedKernel.Persistence;

/// <summary>
/// P0-1 (C1): cross-layer ambient (connection, transaction) accessor used to
/// make the document-Reverse flow a single atomic transaction.
///
/// The accounting <c>PostgresUnitOfWork</c> publishes its open connection +
/// transaction here while a unit-of-work is in flight. Stores that live in the
/// lower <c>Infrastructure.PostgreSQL</c> layer — which cannot see the
/// accounting-layer <c>PostgresExecutionContextAccessor</c> (its context type
/// is <c>internal</c>) — read <see cref="Current"/> and JOIN the ambient
/// transaction instead of opening their own, so their writes commit (or roll
/// back) together with the rest of the unit of work. When <see cref="Current"/>
/// is null the stores fall back to their original self-managed-transaction path
/// (e.g. the standalone JE Void endpoint and unit tests).
///
/// Mirrors the receipt-flow <c>InventoryReceiptExecutionContextAccessor</c>
/// pattern, but lives in <c>SharedKernel</c> so BOTH the accounting
/// infrastructure (publisher) and the shared PostgreSQL infrastructure
/// (consumers) can reference it without a cross-project dependency.
///
/// AsyncLocal-backed, so a singleton registration is correct: each async flow
/// sees only its own ambient transaction.
/// </summary>
public sealed class AmbientDatabaseTransactionAccessor
{
    private readonly AsyncLocal<AmbientDatabaseTransaction?> _current = new();

    public AmbientDatabaseTransaction? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

public sealed record AmbientDatabaseTransaction(
    NpgsqlConnection Connection,
    NpgsqlTransaction Transaction);
