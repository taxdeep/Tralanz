using Modules.Company.MultiCurrency;
using Npgsql;
using SharedKernel.Company;

namespace Infrastructure.PostgreSQL.Company;

public sealed class PostgreSqlCompanyCurrencyProvisioningStore : ICompanyCurrencyProvisioningStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlCompanyCurrencyProvisioningStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<CompanyCurrencyProfile> GetProfileAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await GetProfileAsync(connection, transaction: null, companyId, cancellationToken);
    }

    public async Task<CompanyControlAccountSlots> AllocateControlAccountSlotsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        var accountCodeLength = await GetAccountCodeLengthAsync(connection, transaction: null, companyId, cancellationToken);

        var arCode = await AllocateNextFamilyCodeAsync(connection, transaction: null, companyId, "11", accountCodeLength, cancellationToken);
        var apCode = await AllocateNextFamilyCodeAsync(connection, transaction: null, companyId, "20", accountCodeLength, cancellationToken);
        return new CompanyControlAccountSlots(arCode, apCode);
    }

    private static async Task<int> GetAccountCodeLengthAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select account_code_length
            from companies
            where id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (raw is null || raw is DBNull)
        {
            throw new InvalidOperationException($"Company {companyId:D} was not found.");
        }
        return Convert.ToInt32(raw);
    }

    /// <summary>
    /// Computes the next free numeric code in a chart-of-accounts family
    /// (e.g. "11" for AR, "20" for AP) at the company's account_code_length.
    /// Skips the family base (e.g. 11000 / 1100 / 110000), then picks
    /// max(existing tail) + 1, where the tail is the last
    /// (accountCodeLength - 2) characters of the code.
    /// </summary>
    private static async Task<string> AllocateNextFamilyCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        string familyPrefix,
        int accountCodeLength,
        CancellationToken cancellationToken)
    {
        if (familyPrefix.Length != 2)
        {
            throw new InvalidOperationException($"Family prefix '{familyPrefix}' must be exactly 2 characters.");
        }
        if (accountCodeLength < 4)
        {
            throw new InvalidOperationException($"Account code length {accountCodeLength} is below the minimum of 4.");
        }

        var tailWidth = accountCodeLength - familyPrefix.Length;
        var baseCode = familyPrefix + new string('0', tailWidth);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select coalesce(
                max(
                    case
                        when substring(code from @prefix_len + 1) ~ '^[0-9]+$'
                            then cast(substring(code from @prefix_len + 1) as integer)
                        else 0
                    end
                ),
                0
            )
            from accounts
            where company_id = @company_id
              and length(code) = @code_length
              and code like @prefix_pattern
              and code <> @base_code;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("prefix_len", familyPrefix.Length);
        command.Parameters.AddWithValue("code_length", accountCodeLength);
        command.Parameters.AddWithValue("prefix_pattern", familyPrefix + "%");
        command.Parameters.AddWithValue("base_code", baseCode);
        var maxTail = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        var nextTail = maxTail + 1;
        var maxTailExclusive = (int)Math.Pow(10, tailWidth);
        if (nextTail >= maxTailExclusive)
        {
            throw new InvalidOperationException(
                $"No free codes left in family '{familyPrefix}' for company {companyId:D} at account_code_length={accountCodeLength}.");
        }
        return familyPrefix + nextTail.ToString().PadLeft(tailWidth, '0');
    }

    public async Task<CompanyCurrencyGovernanceResult> EnableCurrencyAsync(
        CompanyId companyId,
        string currencyCode,
        IReadOnlyList<ControlAccountProvisioningRequest> controlAccounts,
        CancellationToken cancellationToken)
    {
        var normalizedCurrencyCode = NormalizeCurrencyCode(currencyCode);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var companyRow = await GetCompanyRowAsync(connection, transaction, companyId, cancellationToken);
        await EnsureCurrencyExistsAsync(connection, transaction, normalizedCurrencyCode, cancellationToken);
        await EnsureCompanyCurrencyEnabledAsync(connection, transaction, companyId, companyRow.BaseCurrencyCode, cancellationToken);
        await EnsureCompanyCurrencyEnabledAsync(connection, transaction, companyId, normalizedCurrencyCode, cancellationToken);

        if (!string.Equals(companyRow.BaseCurrencyCode, normalizedCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            await EnableMultiCurrencyAsync(connection, transaction, companyId, cancellationToken);
        }

        var provisionedAccounts = new List<ProvisionedControlAccount>(controlAccounts.Count);
        foreach (var controlAccount in controlAccounts)
        {
            provisionedAccounts.Add(await EnsureControlAccountAsync(
                connection,
                transaction,
                companyId,
                controlAccount,
                cancellationToken));
        }

        var profile = await GetProfileAsync(connection, transaction, companyId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CompanyCurrencyGovernanceResult(profile, provisionedAccounts);
    }

    private static async Task EnableMultiCurrencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update companies
            set multi_currency_enabled = true,
                updated_at = now()
            where id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureCurrencyExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select count(*)
            from currency_catalog
            where code = @currency_code
              and is_active = true;
            """;
        command.Parameters.AddWithValue("currency_code", currencyCode);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        if (count == 0)
        {
            throw new InvalidOperationException($"Currency {currencyCode} is not active in the catalog.");
        }
    }

    private static async Task EnsureCompanyCurrencyEnabledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into company_currencies (
              id,
              company_id,
              currency_code,
              is_enabled,
              created_at
            )
            values (
              gen_random_uuid(),
              @company_id,
              @currency_code,
              true,
              now()
            )
            on conflict (company_id, currency_code)
            do update
              set is_enabled = true;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("currency_code", currencyCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ProvisionedControlAccount> EnsureControlAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        ControlAccountProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText =
                """
                select
                  id,
                  code,
                  name,
                  currency_code,
                  system_role,
                  system_key
                from accounts
                where company_id = @company_id
                  and (
                    system_role = @system_role
                    or system_key = @system_key
                    or code = @code
                  )
                order by
                  case
                    when system_role = @system_role then 0
                    when system_key = @system_key then 1
                    else 2
                  end
                limit 1;
                """;
            existingCommand.Parameters.AddWithValue("company_id", companyId.Value);
            existingCommand.Parameters.AddWithValue("system_role", request.SystemRole);
            existingCommand.Parameters.AddWithValue("system_key", request.SystemKey);
            existingCommand.Parameters.AddWithValue("code", request.Code);

            await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var existingSystemRole = reader.IsDBNull(reader.GetOrdinal("system_role"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("system_role"));
                var existingSystemKey = reader.IsDBNull(reader.GetOrdinal("system_key"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("system_key"));
                var existingCode = reader.GetString(reader.GetOrdinal("code"));

                var isGovernedMatch =
                    string.Equals(existingSystemRole, request.SystemRole, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existingSystemKey, request.SystemKey, StringComparison.OrdinalIgnoreCase);

                if (!isGovernedMatch)
                {
                    throw new InvalidOperationException(
                        $"Account code {existingCode} already exists for company {companyId:D} but is not the governed control account {request.SystemRole}.");
                }

                return new ProvisionedControlAccount(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    existingCode,
                    reader.GetString(reader.GetOrdinal("name")),
                    reader.IsDBNull(reader.GetOrdinal("currency_code"))
                        ? string.Empty
                        : reader.GetString(reader.GetOrdinal("currency_code")),
                    existingSystemRole,
                    false);
            }
        }

        var entityNumber = await ReserveEntityNumberAsync(connection, transaction, companyId, DateTime.UtcNow.Year, cancellationToken);
        var accountId = Guid.NewGuid();

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into accounts (
                  id,
                  company_id,
                  entity_number,
                  code,
                  name,
                  root_type,
                  detail_type,
                  is_active,
                  is_system,
                  is_system_default,
                  system_key,
                  system_role,
                  currency_code,
                  allow_manual_posting,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @code,
                  @name,
                  @root_type,
                  @detail_type,
                  true,
                  true,
                  false,
                  @system_key,
                  @system_role,
                  @currency_code,
                  @allow_manual_posting,
                  now(),
                  now()
                );
                """;
            insertCommand.Parameters.AddWithValue("id", accountId);
            insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertCommand.Parameters.AddWithValue("entity_number", entityNumber);
            insertCommand.Parameters.AddWithValue("code", request.Code);
            insertCommand.Parameters.AddWithValue("name", request.Name);
            insertCommand.Parameters.AddWithValue("root_type", request.RootType);
            insertCommand.Parameters.AddWithValue("detail_type", request.DetailType);
            insertCommand.Parameters.AddWithValue("system_key", request.SystemKey);
            insertCommand.Parameters.AddWithValue("system_role", request.SystemRole);
            insertCommand.Parameters.AddWithValue("currency_code", request.CurrencyCode);
            insertCommand.Parameters.AddWithValue("allow_manual_posting", request.AllowManualPosting);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return new ProvisionedControlAccount(
            accountId,
            request.Code,
            request.Name,
            request.CurrencyCode,
            request.SystemRole,
            true);
    }

    private static async Task<CompanyCurrencyProfile> GetProfileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var companyRow = await GetCompanyRowAsync(connection, transaction, companyId, cancellationToken);
        var currencies = new List<CompanyCurrencyOption>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              catalog.code,
              catalog.name,
              case
                when catalog.code = @base_currency_code then true
                else false
              end as is_base_currency,
              case
                when catalog.code = @base_currency_code then true
                when coalesce(company_currency.is_enabled, false) then true
                else false
              end as is_enabled
            from currency_catalog catalog
            left join company_currencies company_currency
              on company_currency.company_id = @company_id
             and company_currency.currency_code = catalog.code
            where catalog.is_active = true
            order by
              case
                when catalog.code = @base_currency_code then 0
                else 1
              end,
              catalog.code asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("base_currency_code", companyRow.BaseCurrencyCode);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            currencies.Add(new CompanyCurrencyOption(
                reader.GetString(reader.GetOrdinal("code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetBoolean(reader.GetOrdinal("is_base_currency")),
                reader.GetBoolean(reader.GetOrdinal("is_enabled"))));
        }

        return new CompanyCurrencyProfile(
            companyId,
            companyRow.LegalName,
            companyRow.BaseCurrencyCode,
            companyRow.MultiCurrencyEnabled,
            currencies);
    }

    private static async Task<(string LegalName, string BaseCurrencyCode, bool MultiCurrencyEnabled)> GetCompanyRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select legal_name, base_currency_code, multi_currency_enabled
            from companies
            where id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Company {companyId:D} was not found.");
        }

        return (
            reader.GetString(reader.GetOrdinal("legal_name")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetBoolean(reader.GetOrdinal("multi_currency_enabled")));
    }

    private static string NormalizeCurrencyCode(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new InvalidOperationException("A currency code is required.");
        }

        return currencyCode.Trim().ToUpperInvariant();
    }

    // Per-company entity number reservation backed by the same advancing
    // company_entity_number_sequences row that PostgresNumberingSequences
    // (Citus.Accounting.Infrastructure) uses for AP/AR/JE writes. Without
    // this, calling find-max here would race against the advancing
    // sequence and trip uq_accounts_company_entity_number when two
    // foreign-currency control accounts get adjacent ordinals.
    //
    // The seed-from-existing scan is preserved (filtered by company_id)
    // so a fresh row whose sequence row hasn't been initialized yet
    // still lands on max(existing)+1 instead of 1.
    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        await EnsureCompanyEntityNumberSequenceInstalledAsync(connection, transaction, cancellationToken);

        var seedNumber = await FindEntitySeedNumberAsync(connection, transaction, companyId, year, cancellationToken);

        await using (var seedCommand = connection.CreateCommand())
        {
            seedCommand.Transaction = transaction;
            seedCommand.CommandText =
                """
                insert into company_entity_number_sequences (company_id, entity_year, next_ordinal)
                values (@company_id, @entity_year, @seed_number)
                on conflict (company_id, entity_year) do update
                  set next_ordinal = greatest(company_entity_number_sequences.next_ordinal, excluded.next_ordinal);
                """;
            seedCommand.Parameters.AddWithValue("company_id", companyId.Value);
            seedCommand.Parameters.AddWithValue("entity_year", year);
            seedCommand.Parameters.AddWithValue("seed_number", seedNumber);
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update company_entity_number_sequences
            set next_ordinal = greatest(next_ordinal, @seed_number) + 1
            where company_id = @company_id and entity_year = @entity_year
            returning next_ordinal - 1 as issued_number;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_year", year);
        command.Parameters.AddWithValue("seed_number", seedNumber);

        var issuedNumber = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? seedNumber);
        return $"EN{year}{Base36.Encode(issuedNumber, 5)}";
    }

    private static async Task EnsureCompanyEntityNumberSequenceInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              to_regclass('company_entity_number_sequences') is not null
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_entity_number_sequences'
                  and column_name = 'company_id')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_entity_number_sequences'
                  and column_name = 'entity_year')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'company_entity_number_sequences'
                  and column_name = 'next_ordinal');
            """;
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                "Entity number sequence schema has not been installed. Apply database migrations before provisioning currency control accounts.");
        }
    }

    private static async Task<long> FindEntitySeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            with entity_numbers as (
              select entity_number from manual_journal_documents where company_id = @company_id
              union all
              select entity_number from journal_entries where company_id = @company_id
              union all
              select entity_number from invoices where company_id = @company_id
              union all
              select entity_number from bills where company_id = @company_id
              union all
              select entity_number from credit_notes where company_id = @company_id
              union all
              select entity_number from vendor_credits where company_id = @company_id
              union all
              select entity_number from receive_payments where company_id = @company_id
              union all
              select entity_number from pay_bills where company_id = @company_id
              union all
              select entity_number from fx_revaluation_batches where company_id = @company_id
              union all
              select entity_number from accounts where company_id = @company_id
            )
            select coalesce(
              max(
                case
                  when entity_number ~ '^EN{{year}}[0-9]+$'
                    then substring(entity_number from 7)::bigint
                  else 0
                end
              ),
              0
            ) + 1
            from entity_numbers;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
    }
}
