using System.Globalization;
using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.Tax;

/// <summary>
/// PostgreSQL implementation of <see cref="ITaxCodeStore"/>. Reads and
/// writes the existing <c>tax_codes</c> table from the migration draft.
///
/// Schema compatibility: <c>EnsureSchemaAsync</c> uses CREATE TABLE
/// IF NOT EXISTS with the full migration-draft column set, so a deploy
/// that already loaded TRALANZ_POSTGRESQL_MIGRATION_DRAFT.sql sees a
/// no-op and a fresh dev database gets the same shape. Inserts supply
/// safe defaults for the columns the V1 UI does not yet expose
/// (entity_number, recoverability_mode, is_recoverable_on_purchase,
/// account refs); Posting-Engine consumers can still read the full
/// row.
/// </summary>
public sealed class PostgreSqlTaxCodeStore(PostgreSqlConnectionFactory connections) : ITaxCodeStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tax_codes (
                id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id                  UUID NOT NULL,
                entity_number               TEXT NOT NULL,
                code                        TEXT NOT NULL,
                name                        TEXT NOT NULL,
                rate_percent                NUMERIC(9,6) NOT NULL,
                applies_to                  TEXT NOT NULL DEFAULT 'both',
                is_recoverable_on_purchase  BOOLEAN NOT NULL DEFAULT FALSE,
                recoverability_mode         TEXT NOT NULL DEFAULT 'full',
                payable_account_id          UUID NULL,
                recoverable_account_id      UUID NULL,
                registration_number         TEXT NULL,
                is_active                   BOOLEAN NOT NULL DEFAULT TRUE,
                created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            -- Backfill for databases created before registration_number
            -- was added to the inline CREATE TABLE above. Without the
            -- explicit ALTER, existing tables stay missing the column
            -- and SELECTs that reference it fail at parse time.
            ALTER TABLE tax_codes ADD COLUMN IF NOT EXISTS registration_number TEXT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS uq_tax_codes_company_code
                ON tax_codes (company_id, code);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_tax_codes_entity_number
                ON tax_codes (entity_number);
            CREATE INDEX IF NOT EXISTS idx_tax_codes_company_active
                ON tax_codes (company_id, is_active);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaxCodeRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var items = new List<TaxCodeRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeInactive
            ? SelectColumns + " WHERE company_id = @company_id ORDER BY is_active DESC, code;"
            : SelectColumns + " WHERE company_id = @company_id AND is_active = TRUE ORDER BY code;";
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Map(reader));
        }
        return items;
    }

    public async Task<TaxCodeRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid taxCodeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE company_id = @company_id AND id = @id LIMIT 1;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", taxCodeId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<TaxCodeRecord> CreateAsync(
        CompanyId companyId,
        TaxCodeUpsertInput input,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var entityNumber = GenerateEntityNumber();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tax_codes (
                id, company_id, entity_number, code, name, rate_percent,
                applies_to, registration_number, is_active, created_at, updated_at)
            VALUES (
                @id, @company_id, @entity_number, @code, @name, @rate_percent,
                @applies_to, @registration_number, @is_active, @now, @now)
            RETURNING id, company_id, entity_number, code, name, rate_percent,
                      applies_to, registration_number, is_active, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", input.Code.Trim());
        command.Parameters.AddWithValue("name", input.Name.Trim());
        command.Parameters.AddWithValue("rate_percent", input.RatePercent);
        command.Parameters.AddWithValue("applies_to", input.AppliesTo);
        command.Parameters.AddWithValue("registration_number",
            string.IsNullOrWhiteSpace(input.RegistrationNumber) ? (object)DBNull.Value : input.RegistrationNumber.Trim());
        command.Parameters.AddWithValue("is_active", input.IsActive);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Tax code insert returned no row.");
        }
        return Map(reader);
    }

    public async Task<TaxCodeRecord?> UpdateAsync(
        CompanyId companyId,
        Guid taxCodeId,
        TaxCodeUpsertInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tax_codes
               SET code = @code,
                   name = @name,
                   rate_percent = @rate_percent,
                   applies_to = @applies_to,
                   registration_number = @registration_number,
                   is_active = @is_active,
                   updated_at = @now
             WHERE company_id = @company_id AND id = @id
            RETURNING id, company_id, entity_number, code, name, rate_percent,
                      applies_to, registration_number, is_active, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", taxCodeId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("code", input.Code.Trim());
        command.Parameters.AddWithValue("name", input.Name.Trim());
        command.Parameters.AddWithValue("rate_percent", input.RatePercent);
        command.Parameters.AddWithValue("applies_to", input.AppliesTo);
        command.Parameters.AddWithValue("registration_number",
            string.IsNullOrWhiteSpace(input.RegistrationNumber) ? (object)DBNull.Value : input.RegistrationNumber.Trim());
        command.Parameters.AddWithValue("is_active", input.IsActive);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<TaxCodeRecord?> SetActiveAsync(
        CompanyId companyId,
        Guid taxCodeId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tax_codes
               SET is_active = @is_active,
                   updated_at = @now
             WHERE company_id = @company_id AND id = @id
            RETURNING id, company_id, entity_number, code, name, rate_percent,
                      applies_to, registration_number, is_active, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", taxCodeId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("is_active", isActive);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>
    /// Builds an entity number that satisfies the migration-draft regex
    /// <c>^EN[0-9]{4}[0-9]{8}$</c>: <c>EN{4-digit-year}{8-digit-random}</c>.
    /// Uniqueness is enforced by the table-level unique index; collisions
    /// are vanishingly rare at 10^8 cardinality and we do a single retry.
    /// </summary>
    private static string GenerateEntityNumber()
    {
        var year = DateTime.UtcNow.Year;
        var seed = Random.Shared.Next(0, (int)EntityNumber.MaxOrdinal + 1);
        return EntityNumber.Create(year, seed).Value;
    }

    private const string SelectColumns = """
        SELECT id, company_id, entity_number, code, name, rate_percent,
               applies_to, registration_number, is_active, created_at, updated_at
        FROM tax_codes
        """;

    private static TaxCodeRecord Map(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: CompanyId.Parse(reader.GetString(1)),
        EntityNumber: reader.GetString(2),
        Code: reader.GetString(3),
        Name: reader.GetString(4),
        RatePercent: reader.GetDecimal(5),
        AppliesTo: reader.GetString(6),
        RegistrationNumber: reader.IsDBNull(7) ? null : reader.GetString(7),
        IsActive: reader.GetBoolean(8),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(9),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(10));
}
