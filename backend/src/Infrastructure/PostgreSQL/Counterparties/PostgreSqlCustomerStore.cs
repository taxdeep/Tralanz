using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.Counterparties;

/// <summary>
/// PostgreSQL backing for <see cref="ICustomerStore"/>. The base
/// <c>customers</c> table comes from the migration draft (line 673);
/// EnsureSchemaAsync layers on the address-decomposition / tax_id /
/// notes / payment_term columns this UI needs without disturbing rows
/// already in production. Idempotent so a redeploy is safe.
/// </summary>
public sealed class PostgreSqlCustomerStore(PostgreSqlConnectionFactory connections) : ICustomerStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS customers (
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
            ALTER TABLE customers ADD COLUMN IF NOT EXISTS address_line     TEXT NULL;
            ALTER TABLE customers ADD COLUMN IF NOT EXISTS city             TEXT NULL;
            ALTER TABLE customers ADD COLUMN IF NOT EXISTS province_state   TEXT NULL;
            ALTER TABLE customers ADD COLUMN IF NOT EXISTS postal_code      TEXT NULL;
            ALTER TABLE customers ADD COLUMN IF NOT EXISTS country          TEXT NULL;
            ALTER TABLE customers ADD COLUMN IF NOT EXISTS tax_id           TEXT NULL;
            ALTER TABLE customers ADD COLUMN IF NOT EXISTS notes            TEXT NULL;
            ALTER TABLE customers ADD COLUMN IF NOT EXISTS payment_term_id  UUID NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS uq_customers_entity_number ON customers (entity_number);
            CREATE INDEX IF NOT EXISTS idx_customers_company_active ON customers (company_id, is_active);
            CREATE INDEX IF NOT EXISTS idx_customers_company_name ON customers (company_id, display_name);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CustomerRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var rows = new List<CustomerRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeInactive
            ? SelectColumns + " WHERE company_id = @company_id ORDER BY display_name ASC;"
            : SelectColumns + " WHERE company_id = @company_id AND is_active = TRUE ORDER BY display_name ASC;";
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Map(reader));
        }
        return rows;
    }

    public async Task<CustomerRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE company_id = @company_id AND id = @id LIMIT 1;";
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", customerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<CustomerRecord> CreateAsync(
        CompanyId companyId,
        CustomerUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO customers (
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
        command.Parameters.AddWithValue("company_id", companyId);
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
            throw new InvalidOperationException("Customer insert returned no row.");
        }
        return Map(reader);
    }

    public async Task<CustomerRecord?> UpdateAsync(
        CompanyId companyId,
        Guid customerId,
        CustomerUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE customers
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
        command.Parameters.AddWithValue("id", customerId);
        command.Parameters.AddWithValue("company_id", companyId);
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

    public async Task<IReadOnlyList<CustomerShippingAddressRecord>> ListShippingAddressHistoryAsync(
        CompanyId companyId,
        Guid customerId,
        int limit,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(limit, 1, 50);

        // Pull all shipping addresses this customer has received on
        // historical quotes + sales_orders (the only AR-side documents
        // that store shipping_*; invoices don't). Group by the full
        // address tuple so identical addresses collapse, and rank by
        // most-recent-use first then by usage count. Empty / mostly-
        // empty rows are filtered by requiring at least an address line
        // or a city — pure ghost rows aren't worth offering.
        const string sql = """
            with all_addresses as (
              select shipping_address_line,
                     shipping_city,
                     shipping_province_state,
                     shipping_postal_code,
                     shipping_country,
                     document_date as used_on
                from quotes
               where company_id = @company_id
                 and customer_id = @customer_id
                 and (coalesce(shipping_address_line, '') <> ''
                      or coalesce(shipping_city, '') <> '')
              union all
              select shipping_address_line,
                     shipping_city,
                     shipping_province_state,
                     shipping_postal_code,
                     shipping_country,
                     document_date as used_on
                from sales_orders
               where company_id = @company_id
                 and customer_id = @customer_id
                 and (coalesce(shipping_address_line, '') <> ''
                      or coalesce(shipping_city, '') <> '')
            )
            select coalesce(shipping_address_line, '') as address_line,
                   coalesce(shipping_city, '') as city,
                   coalesce(shipping_province_state, '') as province_state,
                   coalesce(shipping_postal_code, '') as postal_code,
                   coalesce(shipping_country, '') as country,
                   count(*)::int as usage_count,
                   max(used_on) as last_used_on
              from all_addresses
             group by 1, 2, 3, 4, 5
             order by max(used_on) desc, count(*) desc
             limit @limit;
            """;

        var rows = new List<CustomerShippingAddressRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("limit", clamped);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CustomerShippingAddressRecord(
                AddressLine: NullIfEmpty(reader.GetString(0)),
                City: NullIfEmpty(reader.GetString(1)),
                ProvinceState: NullIfEmpty(reader.GetString(2)),
                PostalCode: NullIfEmpty(reader.GetString(3)),
                Country: NullIfEmpty(reader.GetString(4)),
                UsageCount: reader.GetInt32(5),
                LastUsedOn: reader.GetFieldValue<DateOnly>(6)));
        }
        return rows;

        static string? NullIfEmpty(string s) =>
            string.IsNullOrWhiteSpace(s) ? null : s;
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
          FROM customers
        """;

    private static CustomerRecord Map(NpgsqlDataReader reader) => new(
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
