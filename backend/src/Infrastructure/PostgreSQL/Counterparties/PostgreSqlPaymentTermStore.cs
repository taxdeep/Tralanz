using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.Counterparties;

/// <summary>
/// PostgreSQL backing for <see cref="IPaymentTermStore"/>. Owns the
/// <c>payment_terms</c> table — a per-company catalog keyed by
/// (company_id, code). EnsureSchemaAsync creates the table idempotently
/// so both fresh dev DBs and deploys against the migration draft
/// converge. payment_term_id on vendors is a loose UUID reference (no
/// FK) — same pattern tax_codes use for account refs.
/// </summary>
public sealed class PostgreSqlPaymentTermStore(PostgreSqlConnectionFactory connections) : IPaymentTermStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS payment_terms (
                id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id  UUID NOT NULL,
                code        TEXT NOT NULL,
                name        TEXT NOT NULL,
                net_days    INTEGER NOT NULL DEFAULT 0,
                is_active   BOOLEAN NOT NULL DEFAULT TRUE,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS uq_payment_terms_company_code
                ON payment_terms (company_id, code);
            CREATE INDEX IF NOT EXISTS idx_payment_terms_company_active
                ON payment_terms (company_id, is_active);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PaymentTermRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var rows = new List<PaymentTermRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeInactive
            ? SelectColumns + " WHERE company_id = @company_id ORDER BY is_active DESC, net_days, code;"
            : SelectColumns + " WHERE company_id = @company_id AND is_active = TRUE ORDER BY net_days, code;";
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Map(reader));
        }
        return rows;
    }

    public async Task<PaymentTermRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid paymentTermId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE company_id = @company_id AND id = @id LIMIT 1;";
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", paymentTermId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<PaymentTermRecord> CreateAsync(
        CompanyId companyId,
        PaymentTermUpsertInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO payment_terms (
                company_id, code, name, net_days, is_active, created_at, updated_at)
            VALUES (
                @company_id, @code, @name, @net_days, @is_active, @now, @now)
            RETURNING id, company_id, code, name, net_days, is_active, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("code", input.Code.Trim());
        command.Parameters.AddWithValue("name", input.Name.Trim());
        command.Parameters.AddWithValue("net_days", input.NetDays);
        command.Parameters.AddWithValue("is_active", input.IsActive);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Payment term insert returned no row.");
        }
        return Map(reader);
    }

    public async Task<PaymentTermRecord?> UpdateAsync(
        CompanyId companyId,
        Guid paymentTermId,
        PaymentTermUpsertInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE payment_terms
               SET code       = @code,
                   name       = @name,
                   net_days   = @net_days,
                   is_active  = @is_active,
                   updated_at = @now
             WHERE company_id = @company_id AND id = @id
            RETURNING id, company_id, code, name, net_days, is_active, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", paymentTermId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("code", input.Code.Trim());
        command.Parameters.AddWithValue("name", input.Name.Trim());
        command.Parameters.AddWithValue("net_days", input.NetDays);
        command.Parameters.AddWithValue("is_active", input.IsActive);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<PaymentTermRecord?> SetActiveAsync(
        CompanyId companyId,
        Guid paymentTermId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE payment_terms
               SET is_active  = @is_active,
                   updated_at = @now
             WHERE company_id = @company_id AND id = @id
            RETURNING id, company_id, code, name, net_days, is_active, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", paymentTermId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("is_active", isActive);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private const string SelectColumns = """
        SELECT id, company_id, code, name, net_days, is_active, created_at, updated_at
        FROM payment_terms
        """;

    private static PaymentTermRecord Map(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: reader.GetGuid(1),
        Code: reader.GetString(2),
        Name: reader.GetString(3),
        NetDays: reader.GetInt32(4),
        IsActive: reader.GetBoolean(5),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(6),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(7));
}
