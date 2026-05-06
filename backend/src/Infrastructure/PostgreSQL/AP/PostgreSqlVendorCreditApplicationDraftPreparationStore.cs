using Infrastructure.PostgreSQL.Numbering;
using Modules.AP.VendorCreditApplication;
using Npgsql;

namespace Infrastructure.PostgreSQL.AP;

public sealed class PostgreSqlVendorCreditApplicationDraftPreparationStore : IVendorCreditApplicationDraftPreparationStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlVendorCreditApplicationDraftPreparationStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<IReadOnlyList<VendorCreditApplicationOpenItemCandidate>> ListOpenItemCandidatesAsync(
        CompanyId companyId,
        Guid vendorId,
        string documentCurrencyCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await LoadCandidatesAsync(
            connection,
            transaction: null,
            companyId,
            vendorId,
            documentCurrencyCode,
            openItemIds: null,
            forUpdate: false,
            cancellationToken);
    }

    public async Task<VendorCreditApplicationDraftResult> PrepareDraftAsync(
        VendorCreditApplicationDraftPreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        if (preparation.Lines.Count == 0)
        {
            throw new InvalidOperationException("Vendor credit application draft requires at least one application line.");
        }

        var duplicateSource = preparation.Lines
            .GroupBy(static line => line.SourceVendorCreditOpenItemId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateSource is not null)
        {
            throw new InvalidOperationException("Vendor credit application draft cannot target the same vendor credit source more than once.");
        }

        var duplicateTarget = preparation.Lines
            .GroupBy(static line => line.TargetBillOpenItemId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateTarget is not null)
        {
            throw new InvalidOperationException("Vendor credit application draft cannot target the same bill open item more than once.");
        }

        if (preparation.Lines.Any(static line => line.SourceVendorCreditOpenItemId == line.TargetBillOpenItemId))
        {
            throw new InvalidOperationException("Vendor credit application draft source and target open items must be different.");
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureActiveVendorAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            preparation.Context.VendorId,
            cancellationToken);

        var requestedIds = preparation.Lines
            .SelectMany(static line => new[] { line.SourceVendorCreditOpenItemId, line.TargetBillOpenItemId })
            .Distinct()
            .ToArray();

        var candidates = await LoadCandidatesAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            preparation.Context.VendorId,
            preparation.DocumentCurrencyCode,
            requestedIds,
            forUpdate: true,
            cancellationToken);

        if (candidates.Count != requestedIds.Length)
        {
            throw new InvalidOperationException("Vendor credit application draft contains an invalid or non-open AP target.");
        }

        var companyBaseCurrency = await LoadCompanyBaseCurrencyAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            cancellationToken);
        if (!string.Equals(companyBaseCurrency, preparation.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Vendor credit application draft base currency does not match company base currency.");
        }

        var candidateMap = candidates.ToDictionary(static candidate => candidate.OpenItemId);
        var totalAmount = 0m;
        var realizedFxAmountBase = 0m;

        foreach (var line in preparation.Lines)
        {
            if (!candidateMap.TryGetValue(line.SourceVendorCreditOpenItemId, out var source))
            {
                throw new InvalidOperationException("Vendor credit application draft references an unknown AP vendor credit source.");
            }

            if (!candidateMap.TryGetValue(line.TargetBillOpenItemId, out var target))
            {
                throw new InvalidOperationException("Vendor credit application draft references an unknown AP bill target.");
            }

            if (line.AppliedAmountTx <= 0m)
            {
                throw new InvalidOperationException("Vendor credit application draft line amounts must be positive.");
            }

            if (!string.Equals(source.DocumentCurrencyCode, preparation.DocumentCurrencyCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.DocumentCurrencyCode, preparation.DocumentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Vendor credit application lines must use the same transaction currency as the application document.");
            }

            if (source.VendorId != preparation.Context.VendorId || target.VendorId != preparation.Context.VendorId)
            {
                throw new InvalidOperationException("Vendor credit application lines must target open items from the same vendor.");
            }

            if (source.SourceType != "vendor_credit" || source.BalanceSide != "debit")
            {
                throw new InvalidOperationException("Vendor credit application source lines must reference open vendor-credit AP items.");
            }

            if (target.SourceType != "bill" || target.BalanceSide != "credit")
            {
                throw new InvalidOperationException("Vendor credit application target lines must reference open bill AP items.");
            }

            if (source.Status is not ("open" or "partially_applied") || target.Status is not ("open" or "partially_applied"))
            {
                throw new InvalidOperationException("Vendor credit application lines may only target open AP items.");
            }

            if (line.AppliedAmountTx > source.OpenAmountTx || line.AppliedAmountTx > target.OpenAmountTx)
            {
                throw new InvalidOperationException("Vendor credit application line exceeds the current open amount.");
            }

            var sourceCarryingAmountBase = CalculateCarryingAmountBase(
                line.AppliedAmountTx,
                source.OpenAmountTx,
                source.OpenAmountBase);
            var targetCarryingAmountBase = CalculateCarryingAmountBase(
                line.AppliedAmountTx,
                target.OpenAmountTx,
                target.OpenAmountBase);

            totalAmount += line.AppliedAmountTx;
            realizedFxAmountBase += RoundBase(sourceCarryingAmountBase - targetCarryingAmountBase);
        }

        totalAmount = Math.Round(totalAmount, 6, MidpointRounding.ToEven);
        realizedFxAmountBase = RoundBase(realizedFxAmountBase);

        var documentId = Guid.NewGuid();
        var year = preparation.Context.ApplicationDate.Year;

        var entityNumberSeed = await FindEntitySeedNumberAsync(connection, transaction, year, cancellationToken);
        var entityNumber = await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            $"entity-number:vendor-credit-application:{year}",
            $"EN{year}",
            5,
            entityNumberSeed,
            cancellationToken);

        var applicationSeed = await FindApplicationSeedNumberAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            cancellationToken);
        var applicationNumber = await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            "vendor-credit-application-display",
            "VCA-",
            6,
            applicationSeed,
            cancellationToken);

        await InsertDraftHeaderAsync(
            connection,
            transaction,
            documentId,
            entityNumber,
            applicationNumber,
            preparation,
            totalAmount,
            cancellationToken);

        await InsertDraftLinesAsync(
            connection,
            transaction,
            documentId,
            preparation,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new VendorCreditApplicationDraftResult(
            documentId,
            entityNumber,
            applicationNumber,
            preparation.DocumentCurrencyCode,
            preparation.BaseCurrencyCode,
            totalAmount,
            realizedFxAmountBase,
            preparation.Lines.Count,
            "draft");
    }

    private static async Task<IReadOnlyList<VendorCreditApplicationOpenItemCandidate>> LoadCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid vendorId,
        string documentCurrencyCode,
        Guid[]? openItemIds,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            select
              oi.id,
              oi.vendor_id,
              oi.source_type,
              oi.source_id,
              coalesce(b.bill_number, vc.vendor_credit_number, oi.source_id::text) as display_number,
              coalesce(b.bill_date, vc.vendor_credit_date, oi.due_date) as document_date,
              oi.due_date,
              oi.document_currency_code,
              oi.base_currency_code,
              oi.original_amount_tx,
              oi.open_amount_tx,
              oi.open_amount_base,
              oi.balance_side,
              oi.status
            from ap_open_items oi
            left join bills b
              on oi.source_type = 'bill'
             and b.company_id = oi.company_id
             and b.id = oi.source_id
            left join vendor_credits vc
              on oi.source_type = 'vendor_credit'
             and vc.company_id = oi.company_id
             and vc.id = oi.source_id
            where oi.company_id = @company_id
              and oi.vendor_id = @vendor_id
              and oi.status in ('open', 'partially_applied')
              and oi.open_amount_tx > 0
              and oi.document_currency_code = @document_currency_code
              and oi.source_type in ('bill', 'vendor_credit')
              {(openItemIds is null ? string.Empty : "and oi.id = any(@open_item_ids)")}
            order by oi.balance_side asc, oi.due_date asc nulls first, document_date asc, oi.created_at asc, oi.id asc
            {(forUpdate ? "for update of oi" : string.Empty)};
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
        if (openItemIds is not null)
        {
            command.Parameters.AddWithValue("open_item_ids", openItemIds);
        }

        var candidates = new List<VendorCreditApplicationOpenItemCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new VendorCreditApplicationOpenItemCandidate(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                reader.GetString(reader.GetOrdinal("source_type")),
                reader.GetGuid(reader.GetOrdinal("source_id")),
                reader.GetString(reader.GetOrdinal("display_number")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
                reader.IsDBNull(reader.GetOrdinal("due_date"))
                    ? null
                    : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date")),
                reader.GetString(reader.GetOrdinal("document_currency_code")),
                reader.GetString(reader.GetOrdinal("base_currency_code")),
                reader.GetDecimal(reader.GetOrdinal("original_amount_tx")),
                reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
                reader.GetDecimal(reader.GetOrdinal("open_amount_base")),
                reader.GetString(reader.GetOrdinal("balance_side")),
                reader.GetString(reader.GetOrdinal("status"))));
        }

        return candidates;
    }

    private static async Task EnsureActiveVendorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id
            from vendors
            where company_id = @company_id
              and id = @vendor_id
              and is_active = true
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            throw new InvalidOperationException("Vendor credit application draft references a vendor outside the active company context or an inactive vendor.");
        }
    }

    private static async Task<string> LoadCompanyBaseCurrencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select base_currency_code
            from companies
            where id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            throw new InvalidOperationException("Company not found.");
        }

        return Convert.ToString(scalar) ?? string.Empty;
    }

    private static async Task InsertDraftHeaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        string entityNumber,
        string applicationNumber,
        VendorCreditApplicationDraftPreparation preparation,
        decimal totalAmount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into vendor_credit_applications (
              id,
              company_id,
              entity_number,
              application_number,
              vendor_id,
              status,
              application_date,
              document_currency_code,
              base_currency_code,
              total_amount,
              memo,
              created_by_user_id
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @application_number,
              @vendor_id,
              'draft',
              @application_date,
              @document_currency_code,
              @base_currency_code,
              @total_amount,
              @memo,
              @created_by_user_id
            );
            """;
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", preparation.Context.CompanyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("application_number", applicationNumber);
        command.Parameters.AddWithValue("vendor_id", preparation.Context.VendorId);
        command.Parameters.AddWithValue("application_date", preparation.Context.ApplicationDate);
        command.Parameters.AddWithValue("document_currency_code", preparation.DocumentCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", preparation.BaseCurrencyCode);
        command.Parameters.AddWithValue("total_amount", totalAmount);
        command.Parameters.AddWithValue("memo",
            string.IsNullOrWhiteSpace(preparation.Context.Memo) ? (object)DBNull.Value : preparation.Context.Memo.Trim());
        command.Parameters.AddWithValue("created_by_user_id", preparation.Context.UserId.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDraftLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        VendorCreditApplicationDraftPreparation preparation,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < preparation.Lines.Count; index++)
        {
            var line = preparation.Lines[index];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                insert into vendor_credit_application_lines (
                  company_id,
                  vendor_credit_application_id,
                  line_number,
                  source_vendor_credit_ap_open_item_id,
                  target_bill_ap_open_item_id,
                  applied_amount_tx
                )
                values (
                  @company_id,
                  @vendor_credit_application_id,
                  @line_number,
                  @source_vendor_credit_ap_open_item_id,
                  @target_bill_ap_open_item_id,
                  @applied_amount_tx
                );
                """;
            command.Parameters.AddWithValue("company_id", preparation.Context.CompanyId.Value);
            command.Parameters.AddWithValue("vendor_credit_application_id", documentId);
            command.Parameters.AddWithValue("line_number", index + 1);
            command.Parameters.AddWithValue("source_vendor_credit_ap_open_item_id", line.SourceVendorCreditOpenItemId);
            command.Parameters.AddWithValue("target_bill_ap_open_item_id", line.TargetBillOpenItemId);
            command.Parameters.AddWithValue("applied_amount_tx", line.AppliedAmountTx);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<long> FindApplicationSeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select coalesce(
              max(
                case
                  when application_number ~ '^VCA-[0-9]+$'
                    then substring(application_number from 5)::bigint
                  else null
                end
              ),
              0
            ) + 1
            from vendor_credit_applications
            where company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
    }

    private static async Task<long> FindEntitySeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with all_entities as (
              select entity_number from manual_journal_documents
              union all
              select entity_number from journal_entries
              union all
              select entity_number from invoices
              union all
              select entity_number from bills
              union all
              select entity_number from credit_notes
              union all
              select entity_number from vendor_credits
              union all
              select entity_number from receive_payments
              union all
              select entity_number from pay_bills
              union all
              select entity_number from credit_applications
              union all
              select entity_number from vendor_credit_applications
              union all
              select entity_number from fx_revaluation_batches
              union all
              select entity_number from accounts
            )
            select coalesce(
              max(
                case
                  when entity_number ~ '^EN{year}[0-9]+$'
                    then substring(entity_number from 7)::bigint
                  else null
                end
              ),
              0
            ) + 1
            from all_entities;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
    }

    private static decimal CalculateCarryingAmountBase(
        decimal appliedAmountTx,
        decimal openAmountTx,
        decimal openAmountBase)
    {
        if (appliedAmountTx <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(appliedAmountTx), "Applied amount must be greater than zero.");
        }

        if (openAmountTx <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(openAmountTx), "Open amount must be greater than zero.");
        }

        if (openAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(openAmountBase), "Open base amount must be greater than zero.");
        }

        if (appliedAmountTx > openAmountTx)
        {
            throw new InvalidOperationException("Applied amount cannot exceed the remaining open amount.");
        }

        if (appliedAmountTx == openAmountTx)
        {
            return RoundBase(openAmountBase);
        }

        return RoundBase(openAmountBase * (appliedAmountTx / openAmountTx));
    }

    private static decimal RoundBase(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);
}
