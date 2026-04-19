using Infrastructure.PostgreSQL;
using Npgsql;

namespace Web.Shell.Services;

public sealed class ShellCompanyAccountCatalogClient(PostgreSqlConnectionFactory connections)
{
    public async Task<ShellCompanyAccountCatalogSummary> GetSummaryAsync(
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var companyConfig = await LoadCompanyConfigAsync(connection, null, companyId, cancellationToken);
        var enabledCurrencies = await LoadEnabledCurrenciesAsync(connection, null, companyId, cancellationToken);
        var accounts = await LoadAccountsAsync(connection, null, companyId, cancellationToken);
        return BuildSummary(companyConfig, enabledCurrencies, accounts);
    }

    public async Task<Guid> AddBankAccountAsync(
        Guid companyId,
        ShellCompanyBankAccountCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureEntityNumberSequenceTableAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var companyConfig = await LoadCompanyConfigAsync(connection, transaction, companyId, cancellationToken);
            var enabledCurrencies = await LoadEnabledCurrenciesAsync(connection, transaction, companyId, cancellationToken);
            var accounts = await LoadAccountsAsync(connection, transaction, companyId, cancellationToken);
            var summary = BuildSummary(companyConfig, enabledCurrencies, accounts);

            var validation = ShellCompanyAccountCatalogRules.ValidateCreate(request, summary);
            if (!validation.Succeeded)
            {
                throw new InvalidOperationException(validation.ErrorMessage);
            }

            var nextCode = FindNextBankCode(summary.ActiveBankAccounts, summary.InactiveBankAccounts, companyConfig.AccountCodeLength);
            if (string.IsNullOrWhiteSpace(nextCode))
            {
                throw new InvalidOperationException("No free bank account code remains in the reserved 1000-1099 family.");
            }

            var entityNumber = await ReserveEntityNumberAsync(connection, transaction, DateTime.UtcNow.Year, cancellationToken);
            var bankAccountId = Guid.NewGuid();
            var isSystemDefault = summary.ActiveBankAccounts.Count == 0;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
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
                  'asset',
                  'bank',
                  true,
                  false,
                  @is_system_default,
                  null,
                  null,
                  @currency_code,
                  true,
                  now(),
                  now()
                );
                """;
            command.Parameters.AddWithValue("id", bankAccountId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("code", nextCode);
            command.Parameters.AddWithValue("name", request.Name.Trim());
            command.Parameters.AddWithValue("is_system_default", isSystemDefault);
            command.Parameters.AddWithValue("currency_code", request.CurrencyCode.Trim().ToUpperInvariant());
            await command.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return bankAccountId;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Another bank account already uses the generated company-scoped code.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SetBankAccountActiveAsync(
        Guid companyId,
        Guid accountId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var companyConfig = await LoadCompanyConfigAsync(connection, transaction, companyId, cancellationToken);
            var enabledCurrencies = await LoadEnabledCurrenciesAsync(connection, transaction, companyId, cancellationToken);
            var accounts = await LoadAccountsAsync(connection, transaction, companyId, cancellationToken);
            var summary = BuildSummary(companyConfig, enabledCurrencies, accounts);
            var target = summary.ActiveBankAccounts.Concat(summary.InactiveBankAccounts).FirstOrDefault(item => item.Id == accountId);
            if (target is null)
            {
                throw new InvalidOperationException("The selected bank account could not be found for this company.");
            }

            var validation = ShellCompanyAccountCatalogRules.ValidateActiveStateChange(target, isActive, summary);
            if (!validation.Succeeded)
            {
                throw new InvalidOperationException(validation.ErrorMessage);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                update accounts
                set
                  is_active = @is_active,
                  updated_at = now()
                where id = @account_id
                  and company_id = @company_id
                  and detail_type = 'bank';
                """;
            command.Parameters.AddWithValue("account_id", accountId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("is_active", isActive);

            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (rowsAffected == 0)
            {
                throw new InvalidOperationException("The selected bank account could not be found for this company.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static ShellCompanyAccountCatalogSummary BuildSummary(
        ShellCompanyConfigSummary companyConfig,
        IReadOnlyList<ShellCompanyCurrencyOption> enabledCurrencies,
        IReadOnlyList<ShellCompanyAccountCatalogRow> accounts)
    {
        var activeBankAccounts = new List<ShellCompanyBankAccountSummary>();
        var inactiveBankAccounts = new List<ShellCompanyBankAccountSummary>();
        ShellCompanyControlAccountSummary? receivableControl = null;
        ShellCompanyControlAccountSummary? payableControl = null;

        foreach (var row in accounts)
        {
            if (string.Equals(row.DetailType, "bank", StringComparison.OrdinalIgnoreCase))
            {
                var bankAccount = new ShellCompanyBankAccountSummary
                {
                    Id = row.Id,
                    Code = row.Code,
                    Name = row.Name,
                    CurrencyCode = row.CurrencyCode,
                    IsSystemDefault = row.IsSystemDefault,
                    AllowManualPosting = row.AllowManualPosting,
                    IsActive = row.IsActive
                };

                if (row.IsActive)
                {
                    activeBankAccounts.Add(bankAccount);
                }
                else
                {
                    inactiveBankAccounts.Add(bankAccount);
                }

                continue;
            }

            if (!row.IsActive)
            {
                continue;
            }

            if (string.Equals(row.SystemRole, "accounts_receivable", StringComparison.OrdinalIgnoreCase))
            {
                receivableControl = MapControl(row);
            }
            else if (string.Equals(row.SystemRole, "accounts_payable", StringComparison.OrdinalIgnoreCase))
            {
                payableControl = MapControl(row);
            }
        }

        return new ShellCompanyAccountCatalogSummary
        {
            BaseCurrencyCode = companyConfig.BaseCurrencyCode,
            AccountCodeLength = companyConfig.AccountCodeLength,
            EnabledCurrencies = enabledCurrencies,
            ActiveBankAccounts = activeBankAccounts,
            InactiveBankAccounts = inactiveBankAccounts,
            ReceivableControlAccount = receivableControl,
            PayableControlAccount = payableControl
        };
    }

    private static async Task<ShellCompanyConfigSummary> LoadCompanyConfigAsync(
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
              account_code_length
            from companies
            where id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The active company could not be found.");
        }

        return new ShellCompanyConfigSummary
        {
            BaseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code")),
            AccountCodeLength = reader.GetInt16(reader.GetOrdinal("account_code_length"))
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
            order by code asc;
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

    private static async Task<IReadOnlyList<ShellCompanyAccountCatalogRow>> LoadAccountsAsync(
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
              detail_type,
              system_role,
              currency_code,
              is_system_default,
              allow_manual_posting,
              is_active
            from accounts
            where company_id = @company_id
              and (
                detail_type = 'bank'
                or system_role in ('accounts_receivable', 'accounts_payable'))
            order by
              case when detail_type = 'bank' then 0 else 1 end,
              is_active desc,
              code asc,
              name asc,
              id asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<ShellCompanyAccountCatalogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ShellCompanyAccountCatalogRow
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                Code = reader.GetString(reader.GetOrdinal("code")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                DetailType = reader.GetString(reader.GetOrdinal("detail_type")),
                SystemRole = reader.IsDBNull(reader.GetOrdinal("system_role"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("system_role")),
                CurrencyCode = reader.IsDBNull(reader.GetOrdinal("currency_code"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("currency_code")),
                IsSystemDefault = reader.GetBoolean(reader.GetOrdinal("is_system_default")),
                AllowManualPosting = reader.GetBoolean(reader.GetOrdinal("allow_manual_posting")),
                IsActive = reader.GetBoolean(reader.GetOrdinal("is_active"))
            });
        }

        return items;
    }

    private static string FindNextBankCode(
        IReadOnlyCollection<ShellCompanyBankAccountSummary> activeBankAccounts,
        IReadOnlyCollection<ShellCompanyBankAccountSummary> inactiveBankAccounts,
        int accountCodeLength)
    {
        var usedCodes = activeBankAccounts
            .Concat(inactiveBankAccounts)
            .Select(static item => item.Code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var canonicalCode = 1000; canonicalCode <= 1099; canonicalCode++)
        {
            var formattedCode = FormatAccountCode(canonicalCode.ToString(), accountCodeLength);
            if (!usedCodes.Contains(formattedCode))
            {
                return formattedCode;
            }
        }

        return string.Empty;
    }

    private static string FormatAccountCode(string canonicalCode, int accountCodeLength) =>
        canonicalCode.Trim().Length >= accountCodeLength
            ? canonicalCode.Trim()
            : canonicalCode.Trim().PadRight(accountCodeLength, '0');

    private static ShellCompanyControlAccountSummary MapControl(ShellCompanyAccountCatalogRow row) =>
        new()
        {
            Id = row.Id,
            Code = row.Code,
            Name = row.Name,
            CurrencyCode = row.CurrencyCode,
            SystemRole = row.SystemRole
        };

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

    private sealed record ShellCompanyConfigSummary
    {
        public string BaseCurrencyCode { get; init; } = string.Empty;

        public int AccountCodeLength { get; init; }
    }

    private sealed record ShellCompanyAccountCatalogRow
    {
        public Guid Id { get; init; }

        public string Code { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string DetailType { get; init; } = string.Empty;

        public string SystemRole { get; init; } = string.Empty;

        public string CurrencyCode { get; init; } = string.Empty;

        public bool IsSystemDefault { get; init; }

        public bool AllowManualPosting { get; init; }

        public bool IsActive { get; init; }
    }
}

public sealed record class ShellCompanyAccountCatalogSummary
{
    public string BaseCurrencyCode { get; init; } = string.Empty;

    public int AccountCodeLength { get; init; }

    public IReadOnlyList<ShellCompanyCurrencyOption> EnabledCurrencies { get; init; } = Array.Empty<ShellCompanyCurrencyOption>();

    public IReadOnlyList<ShellCompanyBankAccountSummary> ActiveBankAccounts { get; init; } = Array.Empty<ShellCompanyBankAccountSummary>();

    public IReadOnlyList<ShellCompanyBankAccountSummary> InactiveBankAccounts { get; init; } = Array.Empty<ShellCompanyBankAccountSummary>();

    public ShellCompanyControlAccountSummary? ReceivableControlAccount { get; init; }

    public ShellCompanyControlAccountSummary? PayableControlAccount { get; init; }
}

public sealed record class ShellCompanyBankAccountCreateRequest
{
    public string Name { get; init; } = string.Empty;

    public string CurrencyCode { get; init; } = string.Empty;
}

public sealed record class ShellCompanyCurrencyOption
{
    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string DisplayLabel => $"{Code} - {Name}";
}

public sealed record class ShellCompanyBankAccountSummary
{
    public Guid Id { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string CurrencyCode { get; init; } = string.Empty;

    public bool IsSystemDefault { get; init; }

    public bool AllowManualPosting { get; init; }

    public bool IsActive { get; init; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(CurrencyCode)
        ? $"{Code} - {Name}"
        : $"{Code} - {Name} ({CurrencyCode})";
}

public sealed record class ShellCompanyControlAccountSummary
{
    public Guid Id { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string CurrencyCode { get; init; } = string.Empty;

    public string SystemRole { get; init; } = string.Empty;

    public string DisplayLabel => string.IsNullOrWhiteSpace(CurrencyCode)
        ? $"{Code} - {Name}"
        : $"{Code} - {Name} ({CurrencyCode})";
}
