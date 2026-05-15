using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.Counterparties;

/// <summary>
/// Postgres-backed shipping address book per customer. Lives next to
/// <see cref="PostgreSqlCustomerStore"/> so all customer-scoped writes
/// share one schema namespace and the same connection factory.
///
/// Schema invariant: at most one row per (company_id, customer_id) has
/// <c>is_default = true</c>. The unique partial index enforces it; the
/// SetDefaultAsync + InsertAsync paths clear any previous default in
/// the same transaction so the constraint is never violated mid-write.
/// </summary>
public sealed class PostgreSqlCustomerShippingAddressBookStore(
    PostgreSqlConnectionFactory connections) : ICustomerShippingAddressBookStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await PostgreSqlCounterpartySchemaChecks.EnsureTableColumnsAsync(
            connections,
            "customer_shipping_address_book",
            new[]
            {
                "id",
                "company_id",
                "customer_id",
                "label",
                "address_line",
                "city",
                "province_state",
                "postal_code",
                "country",
                "is_default",
                "created_at",
                "updated_at"
            },
            "Customer shipping address book schema has not been installed. Apply database migrations before using customer shipping addresses.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CustomerShippingAddressBookEntry>> ListAsync(
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, company_id, customer_id, label,
                   address_line, city, province_state, postal_code, country,
                   is_default, created_at, updated_at
              from customer_shipping_address_book
             where company_id = @company_id
               and customer_id = @customer_id
             order by is_default desc, created_at asc;
            """;

        var rows = new List<CustomerShippingAddressBookEntry>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Map(reader));
        }
        return rows;
    }

    public async Task<CustomerShippingAddressBookEntry?> GetAsync(
        CompanyId companyId,
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, company_id, customer_id, label,
                   address_line, city, province_state, postal_code, country,
                   is_default, created_at, updated_at
              from customer_shipping_address_book
             where company_id = @company_id
               and customer_id = @customer_id
               and id = @id
             limit 1;
            """;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("id", addressId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<CustomerShippingAddressBookEntry> InsertAsync(
        CompanyId companyId,
        Guid customerId,
        CustomerShippingAddressBookUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // If the inserted row claims the default flag, blank any
        // existing default first so the unique partial index never
        // sees two rows simultaneously.
        if (request.IsDefault)
        {
            await ClearExistingDefaultAsync(connection, transaction, companyId, customerId, excludeId: null, cancellationToken).ConfigureAwait(false);
        }

        const string sql = """
            insert into customer_shipping_address_book (
                company_id, customer_id, label,
                address_line, city, province_state, postal_code, country,
                is_default
            ) values (
                @company_id, @customer_id, @label,
                @address_line, @city, @province_state, @postal_code, @country,
                @is_default
            )
            returning id, company_id, customer_id, label,
                      address_line, city, province_state, postal_code, country,
                      is_default, created_at, updated_at;
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        BindUpsert(command, companyId, customerId, request);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Insert returned no row.");
        }
        var inserted = Map(reader);
        // Reader needs to be closed before commit on Npgsql.
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<CustomerShippingAddressBookEntry?> UpdateAsync(
        CompanyId companyId,
        Guid customerId,
        Guid addressId,
        CustomerShippingAddressBookUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (request.IsDefault)
        {
            await ClearExistingDefaultAsync(connection, transaction, companyId, customerId, excludeId: addressId, cancellationToken).ConfigureAwait(false);
        }

        const string sql = """
            update customer_shipping_address_book set
                label = @label,
                address_line = @address_line,
                city = @city,
                province_state = @province_state,
                postal_code = @postal_code,
                country = @country,
                is_default = @is_default,
                updated_at = now()
             where company_id = @company_id
               and customer_id = @customer_id
               and id = @id
            returning id, company_id, customer_id, label,
                      address_line, city, province_state, postal_code, country,
                      is_default, created_at, updated_at;
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        BindUpsert(command, companyId, customerId, request);
        command.Parameters.AddWithValue("id", addressId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        CustomerShippingAddressBookEntry? updated = null;
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
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            delete from customer_shipping_address_book
             where company_id = @company_id
               and customer_id = @customer_id
               and id = @id;
            """;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("id", addressId);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<CustomerShippingAddressBookEntry?> SetDefaultAsync(
        CompanyId companyId,
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ClearExistingDefaultAsync(connection, transaction, companyId, customerId, excludeId: addressId, cancellationToken).ConfigureAwait(false);

        const string sql = """
            update customer_shipping_address_book set
                is_default = true,
                updated_at = now()
             where company_id = @company_id
               and customer_id = @customer_id
               and id = @id
            returning id, company_id, customer_id, label,
                      address_line, city, province_state, postal_code, country,
                      is_default, created_at, updated_at;
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("id", addressId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        CustomerShippingAddressBookEntry? updated = null;
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
        Guid customerId,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update customer_shipping_address_book set
                is_default = false,
                updated_at = now()
             where company_id = @company_id
               and customer_id = @customer_id
               and is_default = true
               and (@exclude_id is null or id <> @exclude_id);
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("exclude_id", (object?)excludeId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindUpsert(NpgsqlCommand command, CompanyId companyId, Guid customerId, CustomerShippingAddressBookUpsertRequest request)
    {
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);
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

    private static CustomerShippingAddressBookEntry Map(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: CompanyId.Parse(reader.GetString(1)),
        CustomerId: reader.GetGuid(2),
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
