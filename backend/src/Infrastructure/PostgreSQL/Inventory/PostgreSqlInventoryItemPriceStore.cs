using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryItemPriceStore(PostgreSqlConnectionFactory connections)
    : IInventoryItemPriceStore
{
    private const string SchemaSql =
        """
        create table if not exists inventory_item_prices (
          id                uuid          primary key default gen_random_uuid(),
          company_id        char(7)       not null,
          item_id           uuid          not null,
          currency_code     char(3)       not null,
          unit_price        numeric(20, 4) not null,
          min_quantity      numeric(20, 4) not null default 1,
          effective_from    date          not null,
          effective_to      date          null,
          price_list_code   varchar(32)   null,
          customer_id       uuid          null,
          is_active         boolean       not null default true,
          created_at        timestamptz   not null default now(),
          updated_at        timestamptz   not null default now()
        );

        create index if not exists ix_inventory_item_prices_company_item
          on inventory_item_prices (company_id, item_id);

        -- Resolver hot path: every resolve query funnels through
        -- (company_id, item_id, currency_code, is_active=true). The
        -- partial index keeps it tight on multi-million-row catalogs.
        create index if not exists ix_inventory_item_prices_resolve
          on inventory_item_prices (company_id, item_id, currency_code)
          where is_active = true;
        """;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryItemPrice>> ListAsync(
        CompanyId companyId,
        Guid itemId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, company_id, item_id, currency_code, unit_price, min_quantity,
                   effective_from, effective_to, price_list_code, customer_id,
                   is_active, created_at, updated_at
            from inventory_item_prices
            where company_id = @company_id
              and item_id = @item_id
              and (@include_inactive or is_active = true)
            order by
              -- Surface customer-specific rows first, then price-list-specific,
              -- then by most-recent effective date so the UI ranks the rows
              -- in the same priority the resolver would.
              case when customer_id is not null then 0 else 1 end,
              case when price_list_code is not null then 0 else 1 end,
              effective_from desc,
              min_quantity desc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("include_inactive", includeInactive);

        var rows = new List<InventoryItemPrice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadPrice(reader));
        }

        return rows;
    }

    public async Task<InventoryItemPrice?> GetAsync(
        CompanyId companyId,
        Guid priceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, company_id, item_id, currency_code, unit_price, min_quantity,
                   effective_from, effective_to, price_list_code, customer_id,
                   is_active, created_at, updated_at
            from inventory_item_prices
            where company_id = @company_id
              and id = @id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", priceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPrice(reader) : null;
    }

    public async Task<InventoryItemPrice> UpsertAsync(
        CompanyId companyId,
        Guid itemId,
        InventoryItemPriceUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRequest(request);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        if (normalized.Id is null || normalized.Id == Guid.Empty)
        {
            command.CommandText =
                """
                insert into inventory_item_prices (
                  company_id, item_id, currency_code, unit_price, min_quantity,
                  effective_from, effective_to, price_list_code, customer_id, is_active
                )
                values (
                  @company_id, @item_id, @currency_code, @unit_price, @min_quantity,
                  @effective_from, @effective_to, @price_list_code, @customer_id, @is_active
                )
                returning id, company_id, item_id, currency_code, unit_price, min_quantity,
                          effective_from, effective_to, price_list_code, customer_id,
                          is_active, created_at, updated_at;
                """;
        }
        else
        {
            command.CommandText =
                """
                update inventory_item_prices
                set currency_code = @currency_code,
                    unit_price = @unit_price,
                    min_quantity = @min_quantity,
                    effective_from = @effective_from,
                    effective_to = @effective_to,
                    price_list_code = @price_list_code,
                    customer_id = @customer_id,
                    is_active = @is_active,
                    updated_at = now()
                where company_id = @company_id
                  and item_id = @item_id
                  and id = @id
                returning id, company_id, item_id, currency_code, unit_price, min_quantity,
                          effective_from, effective_to, price_list_code, customer_id,
                          is_active, created_at, updated_at;
                """;
            command.Parameters.AddWithValue("id", normalized.Id.Value);
        }

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("currency_code", normalized.CurrencyCode);
        command.Parameters.AddWithValue("unit_price", normalized.UnitPrice);
        command.Parameters.AddWithValue("min_quantity", normalized.MinQuantity);
        command.Parameters.AddWithValue("effective_from", normalized.EffectiveFrom);
        command.Parameters.AddWithValue("effective_to", normalized.EffectiveTo.HasValue ? (object)normalized.EffectiveTo.Value : DBNull.Value);
        command.Parameters.AddWithValue("price_list_code", normalized.PriceListCode is null ? DBNull.Value : (object)normalized.PriceListCode);
        command.Parameters.AddWithValue("customer_id", normalized.CustomerId.HasValue ? (object)normalized.CustomerId.Value : DBNull.Value);
        command.Parameters.AddWithValue("is_active", normalized.IsActive);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Inventory item price upsert did not return a row for company {companyId:D} item {itemId:D}.");
        }

        return ReadPrice(reader);
    }

    public async Task<bool> SoftDeleteAsync(
        CompanyId companyId,
        Guid priceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update inventory_item_prices
            set is_active = false,
                updated_at = now()
            where company_id = @company_id
              and id = @id
              and is_active = true;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", priceId);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<InventoryItemPriceResolution?> ResolveAsync(
        InventoryItemPriceQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, company_id, item_id, currency_code, unit_price, min_quantity,
                   effective_from, effective_to, price_list_code, customer_id
            from inventory_item_prices
            where company_id = @company_id
              and item_id = @item_id
              and currency_code = @currency_code
              and is_active = true
              and effective_from <= @as_of
              and (effective_to is null or effective_to >= @as_of)
              and min_quantity <= @quantity
              and (
                customer_id is null
                or (@customer_id is not null and customer_id = @customer_id)
              )
              and (
                price_list_code is null
                or (@price_list_code is not null and price_list_code = @price_list_code)
              )
            order by
              -- Customer-specific outranks generic. Inside each tier,
              -- price-list match outranks general. Then highest
              -- min_quantity (matched tier) wins. Tie-break on
              -- most-recent effective_from so a freshly-set price beats
              -- a stale one of the same scope.
              case when customer_id is not null then 0 else 1 end,
              case when price_list_code is not null then 0 else 1 end,
              min_quantity desc,
              effective_from desc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", query.CompanyId);
        command.Parameters.AddWithValue("item_id", query.ItemId);
        command.Parameters.AddWithValue("currency_code", query.CurrencyCode);
        command.Parameters.AddWithValue("as_of", query.AsOf);
        command.Parameters.AddWithValue("quantity", query.Quantity);
        command.Parameters.AddWithValue(
            "customer_id",
            query.CustomerId.HasValue ? (object)query.CustomerId.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "price_list_code",
            query.PriceListCode is null ? DBNull.Value : (object)query.PriceListCode);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var customerOrdinal = reader.GetOrdinal("customer_id");
        var priceListOrdinal = reader.GetOrdinal("price_list_code");
        var effectiveToOrdinal = reader.GetOrdinal("effective_to");

        Guid? customerId = reader.IsDBNull(customerOrdinal) ? null : reader.GetGuid(customerOrdinal);
        string? priceListCode = reader.IsDBNull(priceListOrdinal) ? null : reader.GetString(priceListOrdinal).Trim();
        DateOnly? effectiveTo = reader.IsDBNull(effectiveToOrdinal) ? null : reader.GetFieldValue<DateOnly>(effectiveToOrdinal);

        return new InventoryItemPriceResolution
        {
            PriceId = reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId = CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            ItemId = reader.GetGuid(reader.GetOrdinal("item_id")),
            CurrencyCode = reader.GetString(reader.GetOrdinal("currency_code")).Trim().ToUpperInvariant(),
            UnitPrice = reader.GetDecimal(reader.GetOrdinal("unit_price")),
            MinQuantity = reader.GetDecimal(reader.GetOrdinal("min_quantity")),
            EffectiveFrom = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("effective_from")),
            EffectiveTo = effectiveTo,
            PriceListCode = priceListCode,
            CustomerId = customerId,
            MatchedScope = ClassifyScope(customerId, priceListCode),
        };
    }

    private static InventoryItemPriceScope ClassifyScope(Guid? customerId, string? priceListCode) =>
        (customerId.HasValue, priceListCode is not null) switch
        {
            (true, true) => InventoryItemPriceScope.CustomerPriceList,
            (true, false) => InventoryItemPriceScope.Customer,
            (false, true) => InventoryItemPriceScope.PriceList,
            _ => InventoryItemPriceScope.Generic,
        };

    private static InventoryItemPriceUpsertRequest NormalizeRequest(InventoryItemPriceUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CurrencyCode))
        {
            throw new InvalidOperationException("Currency code is required.");
        }

        var currency = request.CurrencyCode.Trim().ToUpperInvariant();
        if (currency.Length != 3)
        {
            throw new InvalidOperationException($"Currency code must be a 3-letter ISO code; got '{request.CurrencyCode}'.");
        }

        if (request.UnitPrice < 0m)
        {
            throw new InvalidOperationException("Unit price cannot be negative.");
        }

        if (request.MinQuantity <= 0m)
        {
            throw new InvalidOperationException("Minimum quantity must be positive.");
        }

        if (request.EffectiveTo.HasValue && request.EffectiveTo.Value < request.EffectiveFrom)
        {
            throw new InvalidOperationException("EffectiveTo must be on or after EffectiveFrom.");
        }

        return request with
        {
            CurrencyCode = currency,
            PriceListCode = string.IsNullOrWhiteSpace(request.PriceListCode)
                ? null
                : request.PriceListCode.Trim().ToUpperInvariant(),
        };
    }

    private static InventoryItemPrice ReadPrice(NpgsqlDataReader reader)
    {
        var customerOrdinal = reader.GetOrdinal("customer_id");
        var priceListOrdinal = reader.GetOrdinal("price_list_code");
        var effectiveToOrdinal = reader.GetOrdinal("effective_to");

        return new InventoryItemPrice(
            Id: reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            ItemId: reader.GetGuid(reader.GetOrdinal("item_id")),
            CurrencyCode: reader.GetString(reader.GetOrdinal("currency_code")).Trim().ToUpperInvariant(),
            UnitPrice: reader.GetDecimal(reader.GetOrdinal("unit_price")),
            MinQuantity: reader.GetDecimal(reader.GetOrdinal("min_quantity")),
            EffectiveFrom: reader.GetFieldValue<DateOnly>(reader.GetOrdinal("effective_from")),
            EffectiveTo: reader.IsDBNull(effectiveToOrdinal) ? null : reader.GetFieldValue<DateOnly>(effectiveToOrdinal),
            PriceListCode: reader.IsDBNull(priceListOrdinal) ? null : reader.GetString(priceListOrdinal).Trim(),
            CustomerId: reader.IsDBNull(customerOrdinal) ? null : reader.GetGuid(customerOrdinal),
            IsActive: reader.GetBoolean(reader.GetOrdinal("is_active")),
            CreatedAtUtc: ToUtc(reader.GetFieldValue<DateTime>(reader.GetOrdinal("created_at"))),
            UpdatedAtUtc: ToUtc(reader.GetFieldValue<DateTime>(reader.GetOrdinal("updated_at"))));
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
}
