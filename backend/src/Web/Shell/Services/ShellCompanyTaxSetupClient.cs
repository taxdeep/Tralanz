using Infrastructure.PostgreSQL;
using Npgsql;

namespace Web.Shell.Services;

public sealed class ShellCompanyTaxSetupClient(PostgreSqlConnectionFactory connections)
{
    public async Task<ShellCompanyTaxSetupSummary> GetSummaryAsync(
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var taxCodes = await LoadTaxCodesAsync(connection, null, companyId, cancellationToken);
        var accountOptions = await LoadAccountOptionsAsync(connection, null, companyId, cancellationToken);
        return BuildSummary(taxCodes, accountOptions.PayableAccountOptions, accountOptions.RecoverableAccountOptions);
    }

    public async Task<Guid> SaveAsync(
        Guid companyId,
        ShellCompanyTaxCodeUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureEntityNumberSequenceTableAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var taxCodes = await LoadTaxCodesAsync(connection, transaction, companyId, cancellationToken);
            var accountOptions = await LoadAccountOptionsAsync(connection, transaction, companyId, cancellationToken);
            var validation = ShellCompanyTaxSetupRules.Validate(
                request,
                taxCodes,
                accountOptions.PayableAccountOptions,
                accountOptions.RecoverableAccountOptions);

            if (!validation.Succeeded)
            {
                throw new InvalidOperationException(validation.ErrorMessage);
            }

            var normalizedCode = request.Code.Trim().ToUpperInvariant();
            var normalizedName = request.Name.Trim();
            var normalizedAppliesTo = request.AppliesTo.Trim().ToLowerInvariant();
            var normalizedRecoverabilityMode = request.RecoverabilityMode.Trim().ToLowerInvariant();
            var isRecoverableOnPurchase =
                ShellCompanyTaxSetupRules.HasPurchaseScope(normalizedAppliesTo) &&
                !string.Equals(normalizedRecoverabilityMode, ShellCompanyTaxSetupRules.RecoverabilityNone, StringComparison.OrdinalIgnoreCase);
            var recoverableAccountId = isRecoverableOnPurchase ? request.RecoverableAccountId : null;

            if (request.Id.HasValue)
            {
                await using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText =
                    """
                    update tax_codes
                    set
                      code = @code,
                      name = @name,
                      rate_percent = @rate_percent,
                      applies_to = @applies_to,
                      is_recoverable_on_purchase = @is_recoverable_on_purchase,
                      recoverability_mode = @recoverability_mode,
                      payable_account_id = @payable_account_id,
                      recoverable_account_id = @recoverable_account_id,
                      updated_at = now()
                    where id = @tax_code_id
                      and company_id = @company_id;
                    """;
                updateCommand.Parameters.AddWithValue("tax_code_id", request.Id.Value);
                updateCommand.Parameters.AddWithValue("company_id", companyId);
                updateCommand.Parameters.AddWithValue("code", normalizedCode);
                updateCommand.Parameters.AddWithValue("name", normalizedName);
                updateCommand.Parameters.AddWithValue("rate_percent", request.RatePercent);
                updateCommand.Parameters.AddWithValue("applies_to", normalizedAppliesTo);
                updateCommand.Parameters.AddWithValue("is_recoverable_on_purchase", isRecoverableOnPurchase);
                updateCommand.Parameters.AddWithValue("recoverability_mode", normalizedRecoverabilityMode);
                updateCommand.Parameters.AddWithValue("payable_account_id", request.PayableAccountId!.Value);
                updateCommand.Parameters.AddWithValue(
                    "recoverable_account_id",
                    recoverableAccountId.HasValue ? recoverableAccountId.Value : DBNull.Value);

                var rowsAffected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException("The selected tax code could not be found for this company.");
                }

                await transaction.CommitAsync(cancellationToken);
                return request.Id.Value;
            }

            var taxCodeId = Guid.NewGuid();
            var entityNumber = await ReserveEntityNumberAsync(connection, transaction, DateTime.UtcNow.Year, cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into tax_codes (
                  id,
                  company_id,
                  entity_number,
                  code,
                  name,
                  rate_percent,
                  applies_to,
                  is_recoverable_on_purchase,
                  recoverability_mode,
                  payable_account_id,
                  recoverable_account_id,
                  is_active,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @code,
                  @name,
                  @rate_percent,
                  @applies_to,
                  @is_recoverable_on_purchase,
                  @recoverability_mode,
                  @payable_account_id,
                  @recoverable_account_id,
                  true,
                  now(),
                  now()
                );
                """;
            insertCommand.Parameters.AddWithValue("id", taxCodeId);
            insertCommand.Parameters.AddWithValue("company_id", companyId);
            insertCommand.Parameters.AddWithValue("entity_number", entityNumber);
            insertCommand.Parameters.AddWithValue("code", normalizedCode);
            insertCommand.Parameters.AddWithValue("name", normalizedName);
            insertCommand.Parameters.AddWithValue("rate_percent", request.RatePercent);
            insertCommand.Parameters.AddWithValue("applies_to", normalizedAppliesTo);
            insertCommand.Parameters.AddWithValue("is_recoverable_on_purchase", isRecoverableOnPurchase);
            insertCommand.Parameters.AddWithValue("recoverability_mode", normalizedRecoverabilityMode);
            insertCommand.Parameters.AddWithValue("payable_account_id", request.PayableAccountId!.Value);
            insertCommand.Parameters.AddWithValue(
                "recoverable_account_id",
                recoverableAccountId.HasValue ? recoverableAccountId.Value : DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return taxCodeId;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("A tax code with the same company-scoped code already exists.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SetActiveAsync(
        Guid companyId,
        Guid taxCodeId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update tax_codes
            set
              is_active = @is_active,
              updated_at = now()
            where id = @tax_code_id
              and company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("tax_code_id", taxCodeId);
        command.Parameters.AddWithValue("is_active", isActive);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rowsAffected == 0)
        {
            throw new InvalidOperationException("The selected tax code could not be found for this company.");
        }
    }

    private static ShellCompanyTaxSetupSummary BuildSummary(
        IReadOnlyList<ShellCompanyManagedTaxCodeSummary> taxCodes,
        IReadOnlyList<ShellCompanyTaxAccountOption> payableAccountOptions,
        IReadOnlyList<ShellCompanyTaxAccountOption> recoverableAccountOptions) =>
        new()
        {
            TaxCodes = taxCodes,
            PayableAccountOptions = payableAccountOptions,
            RecoverableAccountOptions = recoverableAccountOptions
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

    private static async Task<IReadOnlyList<ShellCompanyManagedTaxCodeSummary>> LoadTaxCodesAsync(
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
              tc.id,
              tc.code,
              tc.name,
              tc.rate_percent,
              tc.applies_to,
              tc.is_recoverable_on_purchase,
              tc.recoverability_mode,
              tc.payable_account_id,
              pa.code as payable_account_code,
              pa.name as payable_account_name,
              tc.recoverable_account_id,
              ra.code as recoverable_account_code,
              ra.name as recoverable_account_name,
              tc.is_active
            from tax_codes tc
            left join accounts pa
              on pa.id = tc.payable_account_id
            left join accounts ra
              on ra.id = tc.recoverable_account_id
            where tc.company_id = @company_id
            order by
              tc.is_active desc,
              tc.code asc,
              tc.name asc,
              tc.id asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<ShellCompanyManagedTaxCodeSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ShellCompanyManagedTaxCodeSummary
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                Code = reader.GetString(reader.GetOrdinal("code")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                RatePercent = reader.GetDecimal(reader.GetOrdinal("rate_percent")),
                AppliesTo = reader.GetString(reader.GetOrdinal("applies_to")),
                IsRecoverableOnPurchase = reader.GetBoolean(reader.GetOrdinal("is_recoverable_on_purchase")),
                RecoverabilityMode = reader.GetString(reader.GetOrdinal("recoverability_mode")),
                PayableAccountId = reader.IsDBNull(reader.GetOrdinal("payable_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("payable_account_id")),
                PayableAccountLabel = reader.IsDBNull(reader.GetOrdinal("payable_account_code"))
                    ? string.Empty
                    : $"{reader.GetString(reader.GetOrdinal("payable_account_code"))} - {reader.GetString(reader.GetOrdinal("payable_account_name"))}",
                RecoverableAccountId = reader.IsDBNull(reader.GetOrdinal("recoverable_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("recoverable_account_id")),
                RecoverableAccountLabel = reader.IsDBNull(reader.GetOrdinal("recoverable_account_code"))
                    ? string.Empty
                    : $"{reader.GetString(reader.GetOrdinal("recoverable_account_code"))} - {reader.GetString(reader.GetOrdinal("recoverable_account_name"))}",
                IsActive = reader.GetBoolean(reader.GetOrdinal("is_active"))
            });
        }

        return items;
    }

    private static async Task<ShellCompanyTaxAccountOptions> LoadAccountOptionsAsync(
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
              system_role,
              currency_code,
              is_system_default,
              allow_manual_posting
            from accounts
            where company_id = @company_id
              and is_active = true
              and root_type in ('asset', 'liability')
            order by
              case when is_system_default then 0 else 1 end,
              code asc,
              name asc,
              id asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var payableAccountOptions = new List<ShellCompanyTaxAccountOption>();
        var recoverableAccountOptions = new List<ShellCompanyTaxAccountOption>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var option = new ShellCompanyTaxAccountOption
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                Code = reader.GetString(reader.GetOrdinal("code")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                RootType = reader.GetString(reader.GetOrdinal("root_type")),
                DetailType = reader.GetString(reader.GetOrdinal("detail_type")),
                SystemRole = reader.IsDBNull(reader.GetOrdinal("system_role"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("system_role")),
                CurrencyCode = reader.IsDBNull(reader.GetOrdinal("currency_code"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("currency_code")),
                IsSystemDefault = reader.GetBoolean(reader.GetOrdinal("is_system_default")),
                AllowManualPosting = reader.GetBoolean(reader.GetOrdinal("allow_manual_posting"))
            };

            if (string.Equals(option.RootType, "liability", StringComparison.OrdinalIgnoreCase))
            {
                payableAccountOptions.Add(option);
            }
            else if (string.Equals(option.RootType, "asset", StringComparison.OrdinalIgnoreCase))
            {
                recoverableAccountOptions.Add(option);
            }
        }

        return new ShellCompanyTaxAccountOptions
        {
            PayableAccountOptions = payableAccountOptions,
            RecoverableAccountOptions = recoverableAccountOptions
        };
    }

    private sealed record ShellCompanyTaxAccountOptions
    {
        public IReadOnlyList<ShellCompanyTaxAccountOption> PayableAccountOptions { get; init; } = Array.Empty<ShellCompanyTaxAccountOption>();

        public IReadOnlyList<ShellCompanyTaxAccountOption> RecoverableAccountOptions { get; init; } = Array.Empty<ShellCompanyTaxAccountOption>();
    }
}

public sealed record class ShellCompanyTaxSetupSummary
{
    public IReadOnlyList<ShellCompanyManagedTaxCodeSummary> TaxCodes { get; init; } = Array.Empty<ShellCompanyManagedTaxCodeSummary>();

    public IReadOnlyList<ShellCompanyTaxAccountOption> PayableAccountOptions { get; init; } = Array.Empty<ShellCompanyTaxAccountOption>();

    public IReadOnlyList<ShellCompanyTaxAccountOption> RecoverableAccountOptions { get; init; } = Array.Empty<ShellCompanyTaxAccountOption>();

    public IReadOnlyList<ShellCompanyManagedTaxCodeSummary> ActiveTaxCodes =>
        TaxCodes.Where(static item => item.IsActive).ToArray();

    public IReadOnlyList<ShellCompanyManagedTaxCodeSummary> InactiveTaxCodes =>
        TaxCodes.Where(static item => !item.IsActive).ToArray();

    public IReadOnlyList<ShellCompanyManagedTaxCodeSummary> SalesTaxCodes =>
        ActiveTaxCodes
            .Where(static item => !string.Equals(item.AppliesTo, ShellCompanyTaxSetupRules.AppliesToPurchase, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public IReadOnlyList<ShellCompanyManagedTaxCodeSummary> PurchaseTaxCodes =>
        ActiveTaxCodes
            .Where(static item => ShellCompanyTaxSetupRules.HasPurchaseScope(item.AppliesTo))
            .ToArray();

    public int RecoverablePurchaseTaxCodeCount =>
        PurchaseTaxCodes.Count(static item => !string.Equals(item.RecoverabilityMode, ShellCompanyTaxSetupRules.RecoverabilityNone, StringComparison.OrdinalIgnoreCase));

    public bool HasAnyActiveTaxCodes => ActiveTaxCodes.Count > 0;
}

public sealed record class ShellCompanyTaxCodeUpsertRequest
{
    public Guid? Id { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public decimal RatePercent { get; init; }

    public string AppliesTo { get; init; } = ShellCompanyTaxSetupRules.AppliesToBoth;

    public string RecoverabilityMode { get; init; } = ShellCompanyTaxSetupRules.RecoverabilityNone;

    public Guid? PayableAccountId { get; init; }

    public Guid? RecoverableAccountId { get; init; }
}

public sealed record class ShellCompanyManagedTaxCodeSummary
{
    public Guid Id { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public decimal RatePercent { get; init; }

    public string AppliesTo { get; init; } = string.Empty;

    public bool IsRecoverableOnPurchase { get; init; }

    public string RecoverabilityMode { get; init; } = string.Empty;

    public Guid? PayableAccountId { get; init; }

    public string PayableAccountLabel { get; init; } = string.Empty;

    public Guid? RecoverableAccountId { get; init; }

    public string RecoverableAccountLabel { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public string AppliesToLabel =>
        AppliesTo switch
        {
            ShellCompanyTaxSetupRules.AppliesToSales => "Sales only",
            ShellCompanyTaxSetupRules.AppliesToPurchase => "Purchase only",
            _ => "Sales + purchase"
        };

    public string RecoverabilityLabel =>
        RecoverabilityMode switch
        {
            ShellCompanyTaxSetupRules.RecoverabilityFull => "Recoverable",
            ShellCompanyTaxSetupRules.RecoverabilityPartial => "Partially recoverable",
            _ => "Non-recoverable"
        };

    public string StatusLabel => IsActive ? "Active" : "Inactive";
}

public sealed record class ShellCompanyTaxAccountOption
{
    public Guid Id { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string RootType { get; init; } = string.Empty;

    public string DetailType { get; init; } = string.Empty;

    public string SystemRole { get; init; } = string.Empty;

    public string CurrencyCode { get; init; } = string.Empty;

    public bool IsSystemDefault { get; init; }

    public bool AllowManualPosting { get; init; }

    public string DisplayLabel
    {
        get
        {
            var parts = new List<string> { $"{Code} - {Name}" };
            if (!string.IsNullOrWhiteSpace(CurrencyCode))
            {
                parts.Add(CurrencyCode);
            }

            if (!string.IsNullOrWhiteSpace(SystemRole))
            {
                parts.Add(SystemRole);
            }

            if (IsSystemDefault)
            {
                parts.Add("system default");
            }

            return string.Join(" | ", parts);
        }
    }
}
