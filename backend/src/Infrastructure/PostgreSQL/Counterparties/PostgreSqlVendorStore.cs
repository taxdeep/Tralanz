using Citus.Accounting.Application.Abstractions;
using Infrastructure.PostgreSQL.Numbering;
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
        await PostgreSqlCounterpartySchemaChecks.EnsureTableColumnsAsync(
            connections,
            "vendors",
            new[]
            {
                "id",
                "company_id",
                "entity_number",
                "vendor_number",
                "display_name",
                "default_currency_code",
                "email",
                "phone",
                "address_line",
                "city",
                "province_state",
                "postal_code",
                "country",
                "tax_id",
                "notes",
                "payment_term_id",
                "is_active",
                "created_at",
                "updated_at"
            },
            "Vendor schema has not been installed. Apply database migrations before using vendor records.",
            cancellationToken).ConfigureAwait(false);
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // entity_number stays as the platform-wide audit identifier.
        // vendor_number is the operator-facing display code drawn from
        // the company-scoped "vendor-display" numbering scope (VEN-NNNNNN
        // by default). Same shape as the customer-display wiring on the
        // AR side — see PostgreSqlCustomerStore.
        var vendorNumberSeed = await FindVendorNumberSeedAsync(
            connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
        var vendorNumber = await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            companyId,
            "vendor-display",
            "VEN-",
            padding: 6,
            seedNumber: vendorNumberSeed,
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO vendors (
                company_id, entity_number, vendor_number, display_name, default_currency_code,
                email, phone, address_line, city, province_state, postal_code, country,
                tax_id, notes, payment_term_id, is_active
            )
            VALUES (
                @company_id, @entity_number, @vendor_number, @display_name, @default_currency_code,
                @email, @phone, @address_line, @city, @province_state, @postal_code, @country,
                @tax_id, @notes, @payment_term_id, TRUE
            )
            RETURNING id, company_id, entity_number, vendor_number, display_name, default_currency_code,
                      email, phone, address_line, city, province_state, postal_code, country,
                      tax_id, notes, payment_term_id, is_active, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", GenerateEntityNumber());
        command.Parameters.AddWithValue("vendor_number", vendorNumber);
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
        var record = Map(reader);
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
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
            RETURNING id, company_id, entity_number, vendor_number, display_name, default_currency_code,
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
        var seed = Random.Shared.Next(0, (int)EntityNumber.MaxOrdinal + 1);
        return EntityNumber.Create(year, seed).Value;
    }

    // Seed for vendor-display sequence is max(existing) + 1 across this
    // company's vendors. Mirrors PostgreSqlCustomerStore's helper — keeps
    // the sequence in sync if any rows pre-date the wiring.
    private static async Task<long> FindVendorNumberSeedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select coalesce(
              max(
                case
                  when vendor_number ~ '^VEN-[0-9]+$'
                    then substring(vendor_number from 5)::bigint
                  else null
                end
              ),
              0
            ) + 1
            from vendors
            where company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 1L);
    }

    private const string SelectColumns = """
        SELECT id, company_id, entity_number, vendor_number, display_name, default_currency_code,
               email, phone, address_line, city, province_state, postal_code, country,
               tax_id, notes, payment_term_id, is_active, created_at, updated_at
          FROM vendors
        """;

    private static VendorRecord Map(NpgsqlDataReader reader) => new(
        reader.GetGuid(reader.GetOrdinal("id")),
        CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
        reader.GetString(reader.GetOrdinal("entity_number")),
        ReadNullable(reader, "vendor_number"),
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
