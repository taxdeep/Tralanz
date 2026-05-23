using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

/// <summary>
/// M4 (AUDIT_2026-05-20 P2-10): ambient transaction context for the
/// PostReceiptWorkflow's three-step inventory cycle (activation →
/// valuation → emission). When the workflow opens a single tx via
/// <see cref="PostgreSqlInventoryReceiptUnitOfWork"/>, each downstream
/// store reads <see cref="Current"/> and joins the ambient tx instead
/// of opening its own. If <see cref="Current"/> is null the stores
/// fall back to their original self-managed-tx path — back-compat for
/// callers that invoke a single store standalone.
///
/// Mirrors the accounting-layer
/// <c>PostgresExecutionContextAccessor</c> pattern but lives in the
/// inventory infrastructure project so the three receipt stores can
/// reference it without a cross-project dep on
/// <c>Citus.Accounting.Infrastructure</c>.
/// </summary>
public sealed class InventoryReceiptExecutionContextAccessor
{
    private readonly AsyncLocal<InventoryReceiptExecutionContext?> _current = new();

    public InventoryReceiptExecutionContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

public sealed record InventoryReceiptExecutionContext(
    NpgsqlConnection Connection,
    NpgsqlTransaction Transaction);
