namespace Infrastructure.PostgreSQL.Tasks;

/// <summary>
/// Adds the nullable <c>task_id</c> column (plus a partial index) to
/// every AR / AP line table that may attribute itself to a Task for
/// the gross-margin read model: <c>invoice_lines</c>,
/// <c>credit_note_lines</c>, <c>bill_lines</c>,
/// <c>vendor_credit_lines</c>, <c>expense_lines</c>,
/// <c>ap_purchase_order_lines</c>.
///
/// Per-table index strategy:
/// <list type="bullet">
///   <item>Tables that carry <c>company_id</c> directly on the line row
///     (invoice_lines, credit_note_lines, bill_lines,
///     vendor_credit_lines) get a composite
///     <c>(company_id, task_id)</c> partial index. Queries can use
///     either column as the leading predicate and still benefit.</item>
///   <item>Tables that isolate via the parent row instead of carrying
///     <c>company_id</c> themselves (expense_lines,
///     ap_purchase_order_lines) get a single-column <c>(task_id)</c>
///     partial index. Cost-aggregation queries already JOIN to the
///     parent for company isolation, so this is sufficient — and
///     attempting a composite would fail at boot because the column
///     doesn't exist. (This was the original Batch 8 bug: every table
///     got the composite, but two of them have no <c>company_id</c>.)</item>
/// </list>
///
/// Each ALTER is guarded by <c>ALTER TABLE IF EXISTS</c> + <c>ADD
/// COLUMN IF NOT EXISTS</c>; CREATE INDEX uses <c>IF NOT EXISTS</c> —
/// so re-running the initializer is a no-op, and tables that haven't
/// been created yet (fresh DB before AR/AP bootstraps) are silently
/// skipped.
/// </summary>
public sealed class PostgresTaskLinkSchemaInitializer(PostgreSqlConnectionFactory connections)
{
    /// <summary>
    /// (table name, has company_id column on the line row).
    /// </summary>
    private static readonly IReadOnlyList<(string Table, bool HasCompanyId)> LineTables = new[]
    {
        ("invoice_lines",             true),
        ("credit_note_lines",         true),
        ("bill_lines",                true),
        ("vendor_credit_lines",       true),
        ("expense_lines",             false),
        ("ap_purchase_order_lines",   false),
    };

    private int _ensured;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _ensured) == 1)
        {
            return;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);

        foreach (var (table, hasCompanyId) in LineTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = hasCompanyId
                ? $"""
                    alter table if exists {table}
                      add column if not exists task_id uuid null;

                    create index if not exists ix_{table}_company_task
                      on {table} (company_id, task_id)
                      where task_id is not null;
                    """
                : $"""
                    alter table if exists {table}
                      add column if not exists task_id uuid null;

                    create index if not exists ix_{table}_task
                      on {table} (task_id)
                      where task_id is not null;
                    """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        Volatile.Write(ref _ensured, 1);
    }
}
