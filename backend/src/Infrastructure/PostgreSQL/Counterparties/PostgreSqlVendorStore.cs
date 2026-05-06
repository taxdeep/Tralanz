using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.Counterparties;

/// <summary>
/// PostgreSQL backing for <see cref="IVendorStore"/>. Mirrors
/// <see cref="PostgreSqlCustomerStore"/>: the base <c>vendors</c>
/// table comes from the migration draft (line 689); EnsureSchemaAsync
/// layers on the address-decomposition / tax_id / notes / payment_term
/// columns the UI collects. payment_term_id is a loose UUID reference
/// to <c>payment_terms.id</c> (no FK) — same pattern tax_codes use for
/// account refs.
/// </summary>
public sealed class PostgreSqlVendorStore(PostgreSqlConnectionFactory connections) : IVendorStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS vendors (
                id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id               UUID NOT NULL,
                entity_number            TEXT NOT NULL,
                display_name             TEXT NOT NULL,
                default_currency_code    CHAR(3) NOT NULL,
                email                    TEXT NULL,
                phone                    TEXT NULL,
                address                  TEXT NULL,
                is_active                BOOLEAN NOT NULL DEFAULT TRUE,
                currency_locked          BOOLEAN NOT NULL DEFAULT FALSE,
                created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            ALTER TABLE vendors ADD COLUMN IF NOT EXISTS address_line     TEXT NULL;
            ALTER TABLE vendors ADD COLUMN IF NOT EXISTS city             TEXT NULL;
            ALTER TABLE vendors ADD COLUMN IF NOT EXISTS province_state   TEXT NULL;
            ALTER TABLE vendors ADD COLUMN IF NOT EXISTS postal_code      TEXT NULL;
            ALTER TABLE vendors ADD COLUMN IF NOT EXISTS country          TEXT NULL;
            ALTER TABLE vendors ADD COLUMN IF NOT EXISTS tax_id           TEXT NULL;
            ALTER TABLE vendors ADD COLUMN IF NOT EXISTS notes            TEXT NULL;
            ALTER TABLE vendors ADD COLUMN IF NOT EXISTS payment_term_id  UUID NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS uq_vendors_entity_number ON vendors (entity_number);
            CREATE INDEX IF NOT EXISTS idx_vendors_company_active ON vendors (company_id, is_active);
            CREATE INDEX IF NOT EXISTS idx_vendors_company_name ON vendors (company_id, display_name);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VendorRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var rows = new List<VendorRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeInactive
            ? SelectColumns + " WHERE company_id = @company_id ORDER BY display_name ASC;"
            : SelectColumns + " WHERE company_id = @company_id AND is_active = TRUE ORDER BY display_name ASC;";
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Map(reader));
        }
        return rows;
    }

    public async Task<VendorRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE company_id = @company_id AND id = @id LIMIT 1;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", vendorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<VendorRecord> CreateAsync(
        CompanyId companyId,
        VendorUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO vendors (
                company_id, entity_number, display_name, default_currency_code,
                email, phone, address_line, city, province_state, postal_code, country,
                tax_id, notes, payment_term_id, is_active
            )
            VALUES (
                @company_id, @entity_number, @display_name, @default_currency_code,
                @email, @phone, @address_line, @city, @province_state, @postal_code, @country,
                @tax_id, @notes, @payment_term_id, TRUE
            )
            RETURNING id, company_id, entity_number, display_name, default_currency_code,
                      email, phone, address_line, city, province_state, postal_code, country,
                      tax_id, notes, payment_term_id, is_active, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", GenerateEntityNumber());
        command.Parameters.AddWithValue("display_name", request.DisplayName.Trim());
        command.Parameters.AddWithValue("default_currency_code", request.DefaultCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("email", (object?)NormalizeOptional(request.Email) ?? DBNull.Value);
        command.Parameters.AddWithValue("phone", (object?)NormalizeOptional(request.Phone) ?? DBNull.Value);
        command.Parameters.AddWithValue("address_line", (object?)NormalizeOptional(request.AddressLine) ?? DBNull.Value);
        command.Parameters.AddWithValue("city", (object?)NormalizeOptional(request.City) ?? DBNull.Value);
        command.Parameters.AddWithValue("province_state", (object?)NormalizeOptional(request.ProvinceState) ?? DBNull.Value);
        command.Parameters.AddWithValue("postal_code", (object?)NormalizeOptional(request.PostalCode) ?? DBNull.Value);
        command.Parameters.AddWithValue("country", (object?)NormalizeOptional(request.Country) ?? DBNull.Value);
        command.Parameters.AddWithValue("tax_id", (object?)NormalizeOptional(request.TaxId) ?? DBNull.Value);
        command.Parameters.AddWithValue("notes", (object?)NormalizeOptional(request.Notes) ?? DBNull.Value);
        command.Parameters.AddWithValue("payment_term_id", (object?)request.PaymentTermId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Vendor insert returned no row.");
        }
        return Map(reader);
    }

    public async Task<VendorRecord?> UpdateAsync(
        CompanyId companyId,
        Guid vendorId,
        VendorUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE vendors
               SET display_name          = @display_name,
                   default_currency_code = @default_currency_code,
                   email                 = @email,
                   phone                 = @phone,
                   address_line          = @address_line,
                   city                  = @city,
                   province_state        = @province_state,
                   postal_code           = @postal_code,
                   country               = @country,
                   tax_id                = @tax_id,
                   notes                 = @notes,
                   payment_term_id       = @payment_term_id,
                   updated_at            = NOW()
             WHERE company_id = @company_id AND id = @id
            RETURNING id, company_id, entity_number, display_name, default_currency_code,
                      email, phone, address_line, city, province_state, postal_code, country,
                      tax_id, notes, payment_term_id, is_active, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", vendorId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("display_name", request.DisplayName.Trim());
        command.Parameters.AddWithValue("default_currency_code", request.DefaultCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("email", (object?)NormalizeOptional(request.Email) ?? DBNull.Value);
        command.Parameters.AddWithValue("phone", (object?)NormalizeOptional(request.Phone) ?? DBNull.Value);
        command.Parameters.AddWithValue("address_line", (object?)NormalizeOptional(request.AddressLine) ?? DBNull.Value);
        command.Parameters.AddWithValue("city", (object?)NormalizeOptional(request.City) ?? DBNull.Value);
        command.Parameters.AddWithValue("province_state", (object?)NormalizeOptional(request.ProvinceState) ?? DBNull.Value);
        command.Parameters.AddWithValue("postal_code", (object?)NormalizeOptional(request.PostalCode) ?? DBNull.Value);
        command.Parameters.AddWithValue("country", (object?)NormalizeOptional(request.Country) ?? DBNull.Value);
        command.Parameters.AddWithValue("tax_id", (object?)NormalizeOptional(request.TaxId) ?? DBNull.Value);
        command.Parameters.AddWithValue("notes", (object?)NormalizeOptional(request.Notes) ?? DBNull.Value);
        command.Parameters.AddWithValue("payment_term_id", (object?)request.PaymentTermId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GenerateEntityNumber()
    {
        var year = DateTime.UtcNow.Year;
        var seed = Random.Shared.Next(0, 100_000_000);
        return $"EN{year:0000}{seed:00000000}";
    }

    private const string SelectColumns = """
        SELECT id, company_id, entity_number, display_name, default_currency_code,
               email, phone, address_line, city, province_state, postal_code, country,
               tax_id, notes, payment_term_id, is_active, created_at, updated_at
          FROM vendors
        """;

    private static VendorRecord Map(NpgsqlDataReader reader) => new(
        reader.GetGuid(reader.GetOrdinal("id")),
        CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
        reader.GetString(reader.GetOrdinal("entity_number")),
        reader.GetString(reader.GetOrdinal("display_name")),
        reader.GetString(reader.GetOrdinal("default_currency_code")),
        ReadNullable(reader, "email"),
        ReadNullable(reader, "phone"),
        ReadNullable(reader, "address_line"),
        ReadNullable(reader, "city"),
        ReadNullable(reader, "province_state"),
        ReadNullable(reader, "postal_code"),
        ReadNullable(reader, "country"),
        ReadNullable(reader, "tax_id"),
        ReadNullable(reader, "notes"),
        ReadNullableGuid(reader, "payment_term_id"),
        reader.GetBoolean(reader.GetOrdinal("is_active")),
        reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
        reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));

    private static string? ReadNullable(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Guid? ReadNullableGuid(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }
}
