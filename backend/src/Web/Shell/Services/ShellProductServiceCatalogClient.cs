using Infrastructure.PostgreSQL;
using Modules.GL.JournalEntry;
using Npgsql;

namespace Web.Shell.Services;

public sealed class ShellProductServiceCatalogClient(PostgreSqlConnectionFactory connections)
{
    private const string TableName = "company_product_service_catalog";

    public async Task<ShellProductServiceCatalogSummary> GetSummaryAsync(
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureCatalogTableAsync(connection, cancellationToken);

        var items = await LoadItemsAsync(connection, null, companyId, cancellationToken);
        var accountOptions = await LoadAccountOptionsAsync(connection, null, companyId, cancellationToken);
        var salesTaxCodeOptions = await LoadTaxCodeOptionsAsync(connection, null, companyId, "sales", cancellationToken);
        var purchaseTaxCodeOptions = await LoadTaxCodeOptionsAsync(connection, null, companyId, "purchase", cancellationToken);

        return new ShellProductServiceCatalogSummary
        {
            Items = items,
            SalesRevenueAccountOptions = accountOptions.SalesRevenueAccountOptions,
            PurchaseExpenseAccountOptions = accountOptions.PurchaseExpenseAccountOptions,
            SalesTaxCodeOptions = salesTaxCodeOptions,
            PurchaseTaxCodeOptions = purchaseTaxCodeOptions
        };
    }

    public async Task<Guid> SaveAsync(
        Guid companyId,
        ShellProductServiceUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureCatalogTableAsync(connection, cancellationToken);
        await EnsureEntityNumberSequenceTableAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var items = await LoadItemsAsync(connection, transaction, companyId, cancellationToken);
            var accountOptions = await LoadAccountOptionsAsync(connection, transaction, companyId, cancellationToken);
            var salesTaxCodeOptions = await LoadTaxCodeOptionsAsync(connection, transaction, companyId, "sales", cancellationToken);
            var purchaseTaxCodeOptions = await LoadTaxCodeOptionsAsync(connection, transaction, companyId, "purchase", cancellationToken);
            var validation = ShellProductServiceCatalogRules.Validate(
                request,
                items,
                accountOptions.SalesRevenueAccountOptions,
                accountOptions.PurchaseExpenseAccountOptions,
                salesTaxCodeOptions,
                purchaseTaxCodeOptions);

            if (!validation.Succeeded)
            {
                throw new InvalidOperationException(validation.ErrorMessage);
            }

            var normalizedCatalogType = request.CatalogType.Trim().ToLowerInvariant();
            var normalizedName = request.Name.Trim();
            var normalizedDescription = string.IsNullOrWhiteSpace(request.Description)
                ? DBNull.Value
                : (object)request.Description.Trim();

            if (request.Id.HasValue)
            {
                await using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText =
                    $"""
                    update {TableName}
                    set
                      catalog_type = @catalog_type,
                      name = @name,
                      description = @description,
                      sales_revenue_account_id = @sales_revenue_account_id,
                      purchase_expense_account_id = @purchase_expense_account_id,
                      default_sales_tax_code_id = @default_sales_tax_code_id,
                      default_purchase_tax_code_id = @default_purchase_tax_code_id,
                      updated_at = now()
                    where id = @id
                      and company_id = @company_id;
                    """;
                updateCommand.Parameters.AddWithValue("id", request.Id.Value);
                updateCommand.Parameters.AddWithValue("company_id", companyId);
                updateCommand.Parameters.AddWithValue("catalog_type", normalizedCatalogType);
                updateCommand.Parameters.AddWithValue("name", normalizedName);
                updateCommand.Parameters.AddWithValue("description", normalizedDescription);
                updateCommand.Parameters.AddWithValue(
                    "sales_revenue_account_id",
                    request.SalesRevenueAccountId.HasValue ? (object)request.SalesRevenueAccountId.Value : DBNull.Value);
                updateCommand.Parameters.AddWithValue(
                    "purchase_expense_account_id",
                    request.PurchaseExpenseAccountId.HasValue ? (object)request.PurchaseExpenseAccountId.Value : DBNull.Value);
                updateCommand.Parameters.AddWithValue(
                    "default_sales_tax_code_id",
                    request.DefaultSalesTaxCodeId.HasValue ? (object)request.DefaultSalesTaxCodeId.Value : DBNull.Value);
                updateCommand.Parameters.AddWithValue(
                    "default_purchase_tax_code_id",
                    request.DefaultPurchaseTaxCodeId.HasValue ? (object)request.DefaultPurchaseTaxCodeId.Value : DBNull.Value);

                var rowsAffected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException("The selected product or service could not be found for this company.");
                }

                await transaction.CommitAsync(cancellationToken);
                return request.Id.Value;
            }

            var catalogItemId = Guid.NewGuid();
            var entityNumber = await ReserveEntityNumberAsync(connection, transaction, DateTime.UtcNow.Year, cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                $"""
                insert into {TableName} (
                  id,
                  company_id,
                  entity_number,
                  catalog_type,
                  name,
                  description,
                  sales_revenue_account_id,
                  purchase_expense_account_id,
                  default_sales_tax_code_id,
                  default_purchase_tax_code_id,
                  is_active,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @catalog_type,
                  @name,
                  @description,
                  @sales_revenue_account_id,
                  @purchase_expense_account_id,
                  @default_sales_tax_code_id,
                  @default_purchase_tax_code_id,
                  true,
                  now(),
                  now()
                );
                """;
            insertCommand.Parameters.AddWithValue("id", catalogItemId);
            insertCommand.Parameters.AddWithValue("company_id", companyId);
            insertCommand.Parameters.AddWithValue("entity_number", entityNumber);
            insertCommand.Parameters.AddWithValue("catalog_type", normalizedCatalogType);
            insertCommand.Parameters.AddWithValue("name", normalizedName);
            insertCommand.Parameters.AddWithValue("description", normalizedDescription);
            insertCommand.Parameters.AddWithValue(
                "sales_revenue_account_id",
                request.SalesRevenueAccountId.HasValue ? (object)request.SalesRevenueAccountId.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "purchase_expense_account_id",
                request.PurchaseExpenseAccountId.HasValue ? (object)request.PurchaseExpenseAccountId.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "default_sales_tax_code_id",
                request.DefaultSalesTaxCodeId.HasValue ? (object)request.DefaultSalesTaxCodeId.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "default_purchase_tax_code_id",
                request.DefaultPurchaseTaxCodeId.HasValue ? (object)request.DefaultPurchaseTaxCodeId.Value : DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return catalogItemId;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("A product or service with the same company-scoped name already exists.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SetActiveAsync(
        Guid companyId,
        Guid catalogItemId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureCatalogTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            update {TableName}
            set
              is_active = @is_active,
              updated_at = now()
            where id = @id
              and company_id = @company_id;
            """;
        command.Parameters.AddWithValue("id", catalogItemId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("is_active", isActive);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rowsAffected == 0)
        {
            throw new InvalidOperationException("The selected product or service could not be found for this company.");
        }
    }

    private static async Task EnsureCatalogTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            create table if not exists {TableName} (
              id uuid primary key,
              company_id uuid not null,
              entity_number text not null,
              catalog_type text not null,
              name text not null,
              description text null,
              sales_revenue_account_id uuid null,
              purchase_expense_account_id uuid null,
              default_sales_tax_code_id uuid null,
              default_purchase_tax_code_id uuid null,
              is_active boolean not null default true,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint ck_company_product_service_catalog_type
                check (catalog_type in ('product', 'service', 'non_inventory_product'))
            );

            create unique index if not exists ux_company_product_service_catalog_company_name
              on {TableName} (company_id, lower(name));

            create unique index if not exists ux_company_product_service_catalog_company_entity_number
              on {TableName} (company_id, entity_number);

            alter table {TableName}
              drop constraint if exists ck_company_product_service_catalog_type;

            alter table {TableName}
              add constraint ck_company_product_service_catalog_type
              check (catalog_type in ('product', 'service', 'non_inventory_product'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureEntityNumberSequenceTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table if not exists platform_entity_number_sequences (
              entity_year integer primary key,
              next_number bigint not null
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into platform_entity_number_sequences (
              entity_year,
              next_number
            )
            values (
              @entity_year,
              2
            )
            on conflict (entity_year)
            do update
              set next_number = platform_entity_number_sequences.next_number + 1
            returning next_number - 1;
            """;
        command.Parameters.AddWithValue("entity_year", year);
        var sequenceNumber = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
        return $"EN{year}{sequenceNumber.ToString().PadLeft(8, '0')}";
    }

    private static async Task<IReadOnlyList<ShellManagedProductServiceSummary>> LoadItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select
              item.id,
              item.entity_number,
              item.catalog_type,
              item.name,
              item.description,
              item.sales_revenue_account_id,
              sales_account.code as sales_revenue_account_code,
              sales_account.name as sales_revenue_account_name,
              item.purchase_expense_account_id,
              purchase_account.code as purchase_expense_account_code,
              purchase_account.name as purchase_expense_account_name,
              item.default_sales_tax_code_id,
              sales_tax.code as default_sales_tax_code_code,
              sales_tax.name as default_sales_tax_code_name,
              item.default_purchase_tax_code_id,
              purchase_tax.code as default_purchase_tax_code_code,
              purchase_tax.name as default_purchase_tax_code_name,
              item.is_active
            from {TableName} item
            left join accounts sales_account
              on sales_account.id = item.sales_revenue_account_id
            left join accounts purchase_account
              on purchase_account.id = item.purchase_expense_account_id
            left join tax_codes sales_tax
              on sales_tax.id = item.default_sales_tax_code_id
            left join tax_codes purchase_tax
              on purchase_tax.id = item.default_purchase_tax_code_id
            where item.company_id = @company_id
            order by
              item.is_active desc,
              item.name asc,
              item.id asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<ShellManagedProductServiceSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ShellManagedProductServiceSummary
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                EntityNumber = reader.GetString(reader.GetOrdinal("entity_number")),
                CatalogType = reader.GetString(reader.GetOrdinal("catalog_type")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Description = reader.IsDBNull(reader.GetOrdinal("description"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("description")),
                SalesRevenueAccountId = reader.IsDBNull(reader.GetOrdinal("sales_revenue_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("sales_revenue_account_id")),
                SalesRevenueAccountLabel = reader.IsDBNull(reader.GetOrdinal("sales_revenue_account_code"))
                    ? string.Empty
                    : $"{reader.GetString(reader.GetOrdinal("sales_revenue_account_code"))} - {reader.GetString(reader.GetOrdinal("sales_revenue_account_name"))}",
                PurchaseExpenseAccountId = reader.IsDBNull(reader.GetOrdinal("purchase_expense_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("purchase_expense_account_id")),
                PurchaseExpenseAccountLabel = reader.IsDBNull(reader.GetOrdinal("purchase_expense_account_code"))
                    ? string.Empty
                    : $"{reader.GetString(reader.GetOrdinal("purchase_expense_account_code"))} - {reader.GetString(reader.GetOrdinal("purchase_expense_account_name"))}",
                DefaultSalesTaxCodeId = reader.IsDBNull(reader.GetOrdinal("default_sales_tax_code_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_sales_tax_code_id")),
                DefaultSalesTaxCodeLabel = reader.IsDBNull(reader.GetOrdinal("default_sales_tax_code_code"))
                    ? string.Empty
                    : $"{reader.GetString(reader.GetOrdinal("default_sales_tax_code_code"))} - {reader.GetString(reader.GetOrdinal("default_sales_tax_code_name"))}",
                DefaultPurchaseTaxCodeId = reader.IsDBNull(reader.GetOrdinal("default_purchase_tax_code_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_purchase_tax_code_id")),
                DefaultPurchaseTaxCodeLabel = reader.IsDBNull(reader.GetOrdinal("default_purchase_tax_code_code"))
                    ? string.Empty
                    : $"{reader.GetString(reader.GetOrdinal("default_purchase_tax_code_code"))} - {reader.GetString(reader.GetOrdinal("default_purchase_tax_code_name"))}",
                IsActive = reader.GetBoolean(reader.GetOrdinal("is_active"))
            });
        }

        return items;
    }

    private static async Task<ShellProductServiceAccountOptions> LoadAccountOptionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              code,
              name,
              root_type,
              detail_type,
              coalesce(currency_code, '') as currency_code,
              allow_manual_posting
            from accounts
            where company_id = @company_id
              and is_active = true
              and allow_manual_posting = true
            order by code asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<JournalEntryAccountOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var detailType = reader.IsDBNull(reader.GetOrdinal("detail_type"))
                ? reader.GetString(reader.GetOrdinal("root_type"))
                : reader.GetString(reader.GetOrdinal("detail_type"));
            items.Add(new JournalEntryAccountOption
            {
                AccountId = reader.GetGuid(reader.GetOrdinal("id")),
                Code = reader.GetString(reader.GetOrdinal("code")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                RootType = reader.GetString(reader.GetOrdinal("root_type")),
                DetailType = detailType,
                TypeLabel = ToTitle(detailType),
                CurrencyCode = reader.GetString(reader.GetOrdinal("currency_code")),
                AllowManualPosting = reader.GetBoolean(reader.GetOrdinal("allow_manual_posting"))
            });
        }

        return new ShellProductServiceAccountOptions
        {
            SalesRevenueAccountOptions = items.Where(IsSalesRevenueAccount).ToArray(),
            PurchaseExpenseAccountOptions = items.Where(IsPurchaseExpenseAccount).ToArray()
        };
    }

    private static async Task<IReadOnlyList<ShellTaxCodeLookupOption>> LoadTaxCodeOptionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        string appliesTo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              code,
              name,
              rate_percent,
              applies_to,
              is_recoverable_on_purchase
            from tax_codes
            where company_id = @company_id
              and is_active = true
              and (applies_to = @applies_to or applies_to = 'both')
            order by code asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("applies_to", appliesTo);

        var items = new List<ShellTaxCodeLookupOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ShellTaxCodeLookupOption
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                Code = reader.GetString(reader.GetOrdinal("code")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                RatePercent = reader.GetDecimal(reader.GetOrdinal("rate_percent")),
                AppliesTo = reader.GetString(reader.GetOrdinal("applies_to")),
                IsRecoverableOnPurchase = reader.GetBoolean(reader.GetOrdinal("is_recoverable_on_purchase"))
            });
        }

        return items;
    }

    private static bool IsSalesRevenueAccount(JournalEntryAccountOption option) =>
        string.Equals(option.RootType, "income", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.RootType, "revenue", StringComparison.OrdinalIgnoreCase) ||
        option.TypeLabel.Contains("Revenue", StringComparison.OrdinalIgnoreCase);

    private static bool IsPurchaseExpenseAccount(JournalEntryAccountOption option) =>
        string.Equals(option.RootType, "expense", StringComparison.OrdinalIgnoreCase) ||
        option.TypeLabel.Contains("Expense", StringComparison.OrdinalIgnoreCase);

    private static string ToTitle(string value)
    {
        var words = value
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant());

        return string.Join(' ', words);
    }

    private sealed record ShellProductServiceAccountOptions
    {
        public IReadOnlyList<JournalEntryAccountOption> SalesRevenueAccountOptions { get; init; } = Array.Empty<JournalEntryAccountOption>();

        public IReadOnlyList<JournalEntryAccountOption> PurchaseExpenseAccountOptions { get; init; } = Array.Empty<JournalEntryAccountOption>();
    }
}

public sealed record class ShellProductServiceCatalogSummary
{
    public IReadOnlyList<ShellManagedProductServiceSummary> Items { get; init; } = Array.Empty<ShellManagedProductServiceSummary>();

    public IReadOnlyList<JournalEntryAccountOption> SalesRevenueAccountOptions { get; init; } = Array.Empty<JournalEntryAccountOption>();

    public IReadOnlyList<JournalEntryAccountOption> PurchaseExpenseAccountOptions { get; init; } = Array.Empty<JournalEntryAccountOption>();

    public IReadOnlyList<ShellTaxCodeLookupOption> SalesTaxCodeOptions { get; init; } = Array.Empty<ShellTaxCodeLookupOption>();

    public IReadOnlyList<ShellTaxCodeLookupOption> PurchaseTaxCodeOptions { get; init; } = Array.Empty<ShellTaxCodeLookupOption>();

    public IReadOnlyList<ShellManagedProductServiceSummary> ActiveItems =>
        Items.Where(static item => item.IsActive).ToArray();

    public IReadOnlyList<ShellManagedProductServiceSummary> InactiveItems =>
        Items.Where(static item => !item.IsActive).ToArray();

    public IReadOnlyList<ShellManagedProductServiceSummary> ActiveSalesItems =>
        ActiveItems.Where(static item => item.SupportsSales).ToArray();

    public IReadOnlyList<ShellManagedProductServiceSummary> ActivePurchaseItems =>
        ActiveItems.Where(static item => item.SupportsPurchase).ToArray();

    public IReadOnlyList<ShellManagedProductServiceSummary> ActiveInventoryManagedItems =>
        ActiveItems.Where(static item => item.EntersInventoryManagement).ToArray();
}

public sealed record class ShellProductServiceUpsertRequest
{
    public Guid? Id { get; init; }

    public string CatalogType { get; init; } = ShellProductServiceCatalogRules.CatalogTypeService;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public Guid? SalesRevenueAccountId { get; init; }

    public Guid? PurchaseExpenseAccountId { get; init; }

    public Guid? DefaultSalesTaxCodeId { get; init; }

    public Guid? DefaultPurchaseTaxCodeId { get; init; }
}

public sealed record class ShellManagedProductServiceSummary
{
    public Guid Id { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string CatalogType { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public Guid? SalesRevenueAccountId { get; init; }

    public string SalesRevenueAccountLabel { get; init; } = string.Empty;

    public Guid? PurchaseExpenseAccountId { get; init; }

    public string PurchaseExpenseAccountLabel { get; init; } = string.Empty;

    public Guid? DefaultSalesTaxCodeId { get; init; }

    public string DefaultSalesTaxCodeLabel { get; init; } = string.Empty;

    public Guid? DefaultPurchaseTaxCodeId { get; init; }

    public string DefaultPurchaseTaxCodeLabel { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public string CatalogTypeLabel =>
        ShellProductServiceCatalogRules.GetCatalogTypeLabel(CatalogType);

    public bool EntersInventoryManagement =>
        ShellProductServiceCatalogRules.EntersInventoryManagement(CatalogType);

    public bool SupportsSales =>
        SalesRevenueAccountId.HasValue || DefaultSalesTaxCodeId.HasValue;

    public bool SupportsPurchase =>
        PurchaseExpenseAccountId.HasValue || DefaultPurchaseTaxCodeId.HasValue;

    public string EffectiveLineDescription =>
        string.IsNullOrWhiteSpace(Description) ? Name : Description;

    public string DisplayLabel => $"{Name} [{CatalogTypeLabel}]";

    public string SalesDefaultsLabel =>
        BuildDefaultsLabel(SalesRevenueAccountLabel, DefaultSalesTaxCodeLabel);

    public string PurchaseDefaultsLabel =>
        BuildDefaultsLabel(PurchaseExpenseAccountLabel, DefaultPurchaseTaxCodeLabel);

    private static string BuildDefaultsLabel(string accountLabel, string taxLabel)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(accountLabel))
        {
            parts.Add(accountLabel);
        }

        if (!string.IsNullOrWhiteSpace(taxLabel))
        {
            parts.Add(taxLabel);
        }

        return parts.Count == 0 ? "No defaults configured" : string.Join(" | ", parts);
    }
}
