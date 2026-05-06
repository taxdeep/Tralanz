using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.Counterparties;

/// <summary>
/// Postgres-backed shipping address book per vendor. Mirrors
/// <see cref="PostgreSqlCustomerShippingAddressBookStore"/> exactly —
/// keep the two impls structurally identical so any future schema or
/// invariant change applies to both sides in one diff.
/// </summary>
public sealed class PostgreSqlVendorShippingAddressBookStore(
    PostgreSqlConnectionFactory connections) : IVendorShippingAddressBookStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists vendor_shipping_address_book (
                id uuid primary key default gen_random_uuid(),
                company_id uuid not null references companies(id) on delete cascade,
                vendor_id uuid not null references vendors(id) on delete cascade,
                label text,
                address_line text not null default '',
                city text not null default '',
                province_state text not null default '',
                postal_code text not null default '',
                country text not null default '',
                is_default boolean not null default false,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );

            create index if not exists ix_vendor_shipping_address_book_vendor
                on vendor_shipping_address_book (company_id, vendor_id);

            create unique index if not exists uq_vendor_shipping_address_book_default
                on vendor_shipping_address_book (company_id, vendor_id)
                where is_default;
            """;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VendorShippingAddressBookEntry>> ListAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, company_id, vendor_id, label,
                   address_line, city, province_state, postal_code, country,
                   is_default, created_at, updated_at
              from vendor_shipping_address_book
             where company_id = @company_id
               and vendor_id = @vendor_id
             order by is_default desc, created_at asc;
            """;

        var rows = new List<VendorShippingAddressBookEntry>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Map(reader));
        }
        return rows;
    }

    public async Task<VendorShippingAddressBookEntry?> GetAsync(
        CompanyId companyId,
        Guid vendorId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, company_id, vendor_id, label,
                   address_line, city, province_state, postal_code, country,
                   is_default, created_at, updated_at
              from vendor_shipping_address_book
             where company_id = @company_id
               and vendor_id = @vendor_id
               and id = @id
             limit 1;
            """;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("id", addressId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<VendorShippingAddressBookEntry> InsertAsync(
        CompanyId companyId,
        Guid vendorId,
        VendorShippingAddressBookUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (request.IsDefault)
        {
            await ClearExistingDefaultAsync(connection, transaction, companyId, vendorId, excludeId: null, cancellationToken).ConfigureAwait(false);
        }

        const string sql = """
            insert into vendor_shipping_address_book (
                company_id, vendor_id, label,
                address_line, city, province_state, postal_code, country,
                is_default
            ) values (
                @company_id, @vendor_id, @label,
                @address_line, @city, @province_state, @postal_code, @country,
                @is_default
            )
            returning id, company_id, vendor_id, label,
                      address_line, city, province_state, postal_code, country,
                      is_default, created_at, updated_at;
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        BindUpsert(command, companyId, vendorId, request);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Insert returned no row.");
        }
        var inserted = Map(reader);
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<VendorShippingAddressBookEntry?> UpdateAsync(
        CompanyId companyId,
        Guid vendorId,
        Guid addressId,
        VendorShippingAddressBookUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (request.IsDefault)
        {
            await ClearExistingDefaultAsync(connection, transaction, companyId, vendorId, excludeId: addressId, cancellationToken).ConfigureAwait(false);
        }

        const string sql = """
            update vendor_shipping_address_book set
                label = @label,
                address_line = @address_line,
                city = @city,
                province_state = @province_state,
                postal_code = @postal_code,
                country = @country,
                is_default = @is_default,
                updated_at = now()
             where company_id = @company_id
               and vendor_id = @vendor_id
               and id = @id
            returning id, company_id, vendor_id, label,
                      address_line, city, province_state, postal_code, country,
                      is_default, created_at, updated_at;
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        BindUpsert(command, companyId, vendorId, request);
        command.Parameters.AddWithValue("id", addressId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        VendorShippingAddressBookEntry? updated = null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            updated = Map(reader);
        }
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<bool> DeleteAsync(
        CompanyId companyId,
        Guid vendorId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            delete from vendor_shipping_address_book
             where company_id = @company_id
               and vendor_id = @vendor_id
               and id = @id;
            """;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("id", addressId);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<VendorShippingAddressBookEntry?> SetDefaultAsync(
        CompanyId companyId,
        Guid vendorId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ClearExistingDefaultAsync(connection, transaction, companyId, vendorId, excludeId: addressId, cancellationToken).ConfigureAwait(false);

        const string sql = """
            update vendor_shipping_address_book set
                is_default = true,
                updated_at = now()
             where company_id = @company_id
               and vendor_id = @vendor_id
               and id = @id
            returning id, company_id, vendor_id, label,
                      address_line, city, province_state, postal_code, country,
                      is_default, created_at, updated_at;
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("id", addressId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        VendorShippingAddressBookEntry? updated = null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            updated = Map(reader);
        }
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static async Task ClearExistingDefaultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid vendorId,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update vendor_shipping_address_book set
                is_default = false,
                updated_at = now()
             where company_id = @company_id
               and vendor_id = @vendor_id
               and is_default = true
               and (@exclude_id is null or id <> @exclude_id);
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("exclude_id", (object?)excludeId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindUpsert(NpgsqlCommand command, CompanyId companyId, Guid vendorId, VendorShippingAddressBookUpsertRequest request)
    {
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("label", (object?)NormalizeOptional(request.Label) ?? DBNull.Value);
        command.Parameters.AddWithValue("address_line", request.AddressLine?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("city", request.City?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("province_state", request.ProvinceState?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("postal_code", request.PostalCode?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("country", request.Country?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("is_default", request.IsDefault);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    private static VendorShippingAddressBookEntry Map(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: CompanyId.Parse(reader.GetString(1)),
        VendorId: reader.GetGuid(2),
        Label: reader.IsDBNull(3) ? null : NullIfEmpty(reader.GetString(3)),
        AddressLine: NullIfEmpty(reader.GetString(4)),
        City: NullIfEmpty(reader.GetString(5)),
        ProvinceState: NullIfEmpty(reader.GetString(6)),
        PostalCode: NullIfEmpty(reader.GetString(7)),
        Country: NullIfEmpty(reader.GetString(8)),
        IsDefault: reader.GetBoolean(9),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(10),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(11));
}
