using Infrastructure.PostgreSQL;
using Npgsql;

namespace Web.Shell.Services;

public sealed class ShellCounterpartyOnboardingClient(PostgreSqlConnectionFactory connections)
{
    private const string CustomerTable = "customers";
    private const string VendorTable = "vendors";

    public async Task<ShellCounterpartyOnboardingSummary> GetSummaryAsync(
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var companyConfig = await LoadCompanyConfigAsync(connection, null, companyId, cancellationToken);
        var enabledCurrencies = await LoadEnabledCurrenciesAsync(connection, null, companyId, cancellationToken);
        var customers = await LoadCounterpartiesAsync(connection, null, companyId, CustomerTable, cancellationToken);
        var vendors = await LoadCounterpartiesAsync(connection, null, companyId, VendorTable, cancellationToken);

        return new ShellCounterpartyOnboardingSummary
        {
            BaseCurrencyCode = companyConfig.BaseCurrencyCode,
            MultiCurrencyEnabled = companyConfig.MultiCurrencyEnabled,
            EnabledCurrencies = enabledCurrencies,
            Customers = customers,
            Vendors = vendors
        };
    }

    public Task<Guid> CreateCustomerAsync(
        Guid companyId,
        ShellCounterpartyOnboardingCreateRequest request,
        CancellationToken cancellationToken = default) =>
        CreateAsync(companyId, request, CustomerTable, cancellationToken);

    public Task<Guid> CreateVendorAsync(
        Guid companyId,
        ShellCounterpartyOnboardingCreateRequest request,
        CancellationToken cancellationToken = default) =>
        CreateAsync(companyId, request, VendorTable, cancellationToken);

    public Task UpdateCustomerAsync(
        Guid companyId,
        Guid customerId,
        ShellCounterpartyOnboardingCreateRequest request,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(companyId, customerId, request, CustomerTable, cancellationToken);

    public Task UpdateVendorAsync(
        Guid companyId,
        Guid vendorId,
        ShellCounterpartyOnboardingCreateRequest request,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(companyId, vendorId, request, VendorTable, cancellationToken);

    public Task SetCustomerActiveAsync(
        Guid companyId,
        Guid customerId,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetActiveAsync(companyId, customerId, isActive, CustomerTable, cancellationToken);

    public Task SetVendorActiveAsync(
        Guid companyId,
        Guid vendorId,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetActiveAsync(companyId, vendorId, isActive, VendorTable, cancellationToken);

    private async Task<Guid> CreateAsync(
        Guid companyId,
        ShellCounterpartyOnboardingCreateRequest request,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureEntityNumberSequenceTableAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var companyConfig = await LoadCompanyConfigAsync(connection, transaction, companyId, cancellationToken);
            var enabledCurrencies = await LoadEnabledCurrenciesAsync(connection, transaction, companyId, cancellationToken);
            var existingCounterparties = await LoadCounterpartiesAsync(connection, transaction, companyId, tableName, cancellationToken);
            var validation = ShellCounterpartyOnboardingRules.ValidateCreate(
                request,
                existingCounterparties,
                companyConfig.BaseCurrencyCode,
                companyConfig.MultiCurrencyEnabled,
                enabledCurrencies);

            if (!validation.Succeeded)
            {
                throw new InvalidOperationException(validation.ErrorMessage);
            }

            var normalizedDisplayName = request.DisplayName.Trim();
            var currencyCode = companyConfig.MultiCurrencyEnabled
                ? request.CurrencyCode.Trim().ToUpperInvariant()
                : companyConfig.BaseCurrencyCode.Trim().ToUpperInvariant();
            var entityNumber = await ReserveEntityNumberAsync(connection, transaction, DateTime.UtcNow.Year, cancellationToken);
            var counterpartyId = Guid.NewGuid();
            var address = BuildAddress(request);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"""
                insert into {tableName} (
                  id,
                  company_id,
                  entity_number,
                  display_name,
                  default_currency_code,
                  email,
                  phone,
                  address,
                  is_active,
                  currency_locked,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @display_name,
                  @default_currency_code,
                  @email,
                  @phone,
                  @address,
                  true,
                  false,
                  now(),
                  now()
                );
                """;
            command.Parameters.AddWithValue("id", counterpartyId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("display_name", normalizedDisplayName);
            command.Parameters.AddWithValue("default_currency_code", currencyCode);
            command.Parameters.AddWithValue("email", ToDbValue(request.Email));
            command.Parameters.AddWithValue("phone", ToDbValue(request.Phone));
            command.Parameters.AddWithValue("address", ToDbValue(address));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return counterpartyId;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Another counterparty already uses the generated entity number or company-scoped identity.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task UpdateAsync(
        Guid companyId,
        Guid counterpartyId,
        ShellCounterpartyOnboardingCreateRequest request,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var companyConfig = await LoadCompanyConfigAsync(connection, transaction, companyId, cancellationToken);
            var enabledCurrencies = await LoadEnabledCurrenciesAsync(connection, transaction, companyId, cancellationToken);
            var existingCounterparties = await LoadCounterpartiesAsync(connection, transaction, companyId, tableName, cancellationToken);
            var validation = ShellCounterpartyOnboardingRules.ValidateUpdate(
                counterpartyId,
                request,
                existingCounterparties,
                companyConfig.BaseCurrencyCode,
                companyConfig.MultiCurrencyEnabled,
                enabledCurrencies);

            if (!validation.Succeeded)
            {
                throw new InvalidOperationException(validation.ErrorMessage);
            }

            var normalizedDisplayName = request.DisplayName.Trim();
            var currencyCode = companyConfig.MultiCurrencyEnabled
                ? request.CurrencyCode.Trim().ToUpperInvariant()
                : companyConfig.BaseCurrencyCode.Trim().ToUpperInvariant();
            var address = BuildAddress(request);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"""
                update {tableName}
                set
                  display_name = @display_name,
                  default_currency_code = @default_currency_code,
                  email = @email,
                  phone = @phone,
                  address = @address,
                  updated_at = now()
                where company_id = @company_id
                  and id = @id;
                """;
            command.Parameters.AddWithValue("id", counterpartyId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("display_name", normalizedDisplayName);
            command.Parameters.AddWithValue("default_currency_code", currencyCode);
            command.Parameters.AddWithValue("email", ToDbValue(request.Email));
            command.Parameters.AddWithValue("phone", ToDbValue(request.Phone));
            command.Parameters.AddWithValue("address", ToDbValue(address));

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                throw new InvalidOperationException("The selected counterparty could not be found.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task SetActiveAsync(
        Guid companyId,
        Guid counterpartyId,
        bool isActive,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"""
                update {tableName}
                set
                  is_active = @is_active,
                  updated_at = now()
                where company_id = @company_id
                  and id = @id;
                """;
            command.Parameters.AddWithValue("id", counterpartyId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("is_active", isActive);

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                throw new InvalidOperationException("The selected counterparty could not be found.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<ShellCounterpartyCompanyConfig> LoadCompanyConfigAsync(
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
              base_currency_code,
              multi_currency_enabled
            from companies
            where id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The active company could not be found.");
        }

        return new ShellCounterpartyCompanyConfig
        {
            BaseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code")),
            MultiCurrencyEnabled = reader.GetBoolean(reader.GetOrdinal("multi_currency_enabled"))
        };
    }

    private static async Task<IReadOnlyList<ShellCompanyCurrencyOption>> LoadEnabledCurrenciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            with base_company as (
              select base_currency_code
              from companies
              where id = @company_id
            )
            select distinct
              code,
              name
            from (
              select c.base_currency_code as code
              from base_company c
              union
              select cc.currency_code as code
              from company_currencies cc
              where cc.company_id = @company_id
                and cc.is_enabled = true
            ) codes
            join currency_catalog curr
              on curr.code = codes.code
            where curr.is_active = true
            order by codes.code asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<ShellCompanyCurrencyOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ShellCompanyCurrencyOption
            {
                Code = reader.GetString(reader.GetOrdinal("code")),
                Name = reader.GetString(reader.GetOrdinal("name"))
            });
        }

        return items;
    }

    private static async Task<IReadOnlyList<ShellManagedCounterpartySummary>> LoadCounterpartiesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select
              id,
              entity_number,
              display_name,
              default_currency_code,
              email,
              phone,
              address,
              is_active
            from {tableName}
            where company_id = @company_id
            order by
              is_active desc,
              display_name asc,
              id asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<ShellManagedCounterpartySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ShellManagedCounterpartySummary
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                EntityNumber = reader.GetString(reader.GetOrdinal("entity_number")),
                DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
                DefaultCurrencyCode = reader.GetString(reader.GetOrdinal("default_currency_code")),
                Email = reader.IsDBNull(reader.GetOrdinal("email"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("email")),
                Phone = reader.IsDBNull(reader.GetOrdinal("phone"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("phone")),
                Address = reader.IsDBNull(reader.GetOrdinal("address"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("address")),
                IsActive = reader.GetBoolean(reader.GetOrdinal("is_active"))
            });
        }

        return items;
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

    private static string BuildAddress(ShellCounterpartyOnboardingCreateRequest request)
    {
        var lines = new List<string>();
        var addressLine = request.AddressLine.Trim();
        if (!string.IsNullOrWhiteSpace(addressLine))
        {
            lines.Add(addressLine);
        }

        var localitySegments = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.City))
        {
            localitySegments.Add(request.City.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.ProvinceOrState))
        {
            localitySegments.Add(request.ProvinceOrState.Trim());
        }

        var locality = string.Join(", ", localitySegments);
        if (!string.IsNullOrWhiteSpace(request.PostalCode))
        {
            locality = string.IsNullOrWhiteSpace(locality)
                ? request.PostalCode.Trim()
                : $"{locality} {request.PostalCode.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(locality))
        {
            lines.Add(locality);
        }

        if (!string.IsNullOrWhiteSpace(request.Country))
        {
            lines.Add(request.Country.Trim());
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value.Trim();

    private sealed record ShellCounterpartyCompanyConfig
    {
        public string BaseCurrencyCode { get; init; } = string.Empty;

        public bool MultiCurrencyEnabled { get; init; }
    }
}

public sealed record class ShellCounterpartyOnboardingSummary
{
    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool MultiCurrencyEnabled { get; init; }

    public IReadOnlyList<ShellCompanyCurrencyOption> EnabledCurrencies { get; init; } = Array.Empty<ShellCompanyCurrencyOption>();

    public IReadOnlyList<ShellManagedCounterpartySummary> Customers { get; init; } = Array.Empty<ShellManagedCounterpartySummary>();

    public IReadOnlyList<ShellManagedCounterpartySummary> Vendors { get; init; } = Array.Empty<ShellManagedCounterpartySummary>();

    public IReadOnlyList<ShellManagedCounterpartySummary> ActiveCustomers =>
        Customers.Where(static item => item.IsActive).ToArray();

    public IReadOnlyList<ShellManagedCounterpartySummary> ActiveVendors =>
        Vendors.Where(static item => item.IsActive).ToArray();
}

public sealed record class ShellCounterpartyOnboardingCreateRequest
{
    public string DisplayName { get; init; } = string.Empty;

    public string CurrencyCode { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    public string AddressLine { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public string ProvinceOrState { get; init; } = string.Empty;

    public string Country { get; init; } = string.Empty;

    public string PostalCode { get; init; } = string.Empty;
}

public sealed record class ShellManagedCounterpartySummary
{
    public Guid Id { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string DefaultCurrencyCode { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public bool IsActive { get; init; }
}
