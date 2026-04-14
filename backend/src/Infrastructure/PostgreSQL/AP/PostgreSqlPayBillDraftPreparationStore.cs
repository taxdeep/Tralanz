using Infrastructure.PostgreSQL.Numbering;
using Modules.AP.PayBill;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.AP;

public sealed class PostgreSqlPayBillDraftPreparationStore : IPayBillDraftPreparationStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlPayBillDraftPreparationStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<IReadOnlyList<PayBillOpenItemCandidate>> ListOpenItemCandidatesAsync(
        Guid companyId,
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

    public async Task<PayBillDraftResult> PrepareDraftAsync(
        PayBillDraftPreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        if (preparation.Lines.Count == 0)
        {
            throw new InvalidOperationException("Pay bill draft requires at least one application line.");
        }

        var duplicateTarget = preparation.Lines
            .GroupBy(static line => line.TargetOpenItemId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateTarget is not null)
        {
            throw new InvalidOperationException("Pay bill draft cannot target the same open item more than once.");
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureActiveVendorAsync(connection, transaction, preparation.Context.CompanyId, preparation.Context.VendorId, cancellationToken);
        await EnsureActiveBankAccountAsync(connection, transaction, preparation.Context.CompanyId, preparation.Context.BankAccountId, cancellationToken);

        var requestedTargetIds = preparation.Lines.Select(static line => line.TargetOpenItemId).ToArray();
        var candidates = await LoadCandidatesAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            preparation.Context.VendorId,
            preparation.DocumentCurrencyCode,
            requestedTargetIds,
            forUpdate: true,
            cancellationToken);

        if (candidates.Count != requestedTargetIds.Length)
        {
            throw new InvalidOperationException("Pay bill draft contains an invalid or non-open AP target.");
        }

        var candidateMap = candidates.ToDictionary(static candidate => candidate.OpenItemId);
        var totalAmount = 0m;
        foreach (var line in preparation.Lines)
        {
            if (!candidateMap.TryGetValue(line.TargetOpenItemId, out var candidate))
            {
                throw new InvalidOperationException("Pay bill draft references an unknown AP open item.");
            }

            if (line.AppliedAmountTx <= 0m)
            {
                throw new InvalidOperationException("Pay bill draft line amounts must be positive.");
            }

            if (line.AppliedAmountTx > candidate.OpenAmountTx)
            {
                throw new InvalidOperationException("Pay bill draft line exceeds the open AP amount.");
            }

            if (!string.Equals(candidate.DocumentCurrencyCode, preparation.DocumentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Pay bill draft lines must use the same transaction currency.");
            }

            totalAmount += line.AppliedAmountTx;
        }

        var companyBaseCurrency = await LoadCompanyBaseCurrencyAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            cancellationToken);
        if (!string.Equals(companyBaseCurrency, preparation.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pay bill draft base currency does not match company base currency.");
        }

        var documentId = Guid.NewGuid();
        var year = preparation.Context.PaymentDate.Year;

        var entityNumberSeed = await FindEntitySeedNumberAsync(connection, transaction, year, cancellationToken);
        var entityNumber = await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            $"entity-number:pay-bill:{year}",
            $"EN{year}",
            8,
            entityNumberSeed,
            cancellationToken);

        var paymentSeed = await FindPaymentSeedNumberAsync(connection, transaction, preparation.Context.CompanyId, cancellationToken);
        var paymentNumber = await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            preparation.Context.CompanyId,
            "pay-bill-display",
            "PB-",
            6,
            paymentSeed,
            cancellationToken);

        await InsertDraftHeaderAsync(
            connection,
            transaction,
            documentId,
            entityNumber,
            paymentNumber,
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

        return new PayBillDraftResult(
            documentId,
            entityNumber,
            paymentNumber,
            preparation.DocumentCurrencyCode,
            preparation.BaseCurrencyCode,
            preparation.FxResolution.SnapshotId,
            preparation.FxResolution.Rate,
            preparation.FxResolution.RequestedDate,
            preparation.FxResolution.EffectiveDate,
            preparation.FxResolution.SourceSemantics,
            totalAmount,
            preparation.Lines.Count,
            "draft");
    }

    private static async Task<IReadOnlyList<PayBillOpenItemCandidate>> LoadCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        Guid vendorId,
        string documentCurrencyCode,
        Guid[]? openItemIds,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            select
              oi.id,
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
              and oi.balance_side = 'credit'
              and oi.open_amount_tx > 0
              and oi.document_currency_code = @document_currency_code
              {(openItemIds is null ? string.Empty : "and oi.id = any(@open_item_ids)")}
            order by oi.due_date asc nulls first, document_date asc, oi.created_at asc, oi.id asc
            {(forUpdate ? "for update of oi" : string.Empty)};
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);

        if (openItemIds is not null)
        {
            command.Parameters.AddWithValue("open_item_ids", openItemIds);
        }

        var candidates = new List<PayBillOpenItemCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new PayBillOpenItemCandidate(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("source_type")),
                reader.GetGuid(reader.GetOrdinal("source_id")),
                reader.GetString(reader.GetOrdinal("display_number")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
                reader.IsDBNull(reader.GetOrdinal("due_date"))
                    ? (DateOnly?)null
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
        Guid companyId,
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
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("vendor_id", vendorId);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            throw new InvalidOperationException("Pay bill draft references a vendor outside the active company context or an inactive vendor.");
        }
    }

    private static async Task EnsureActiveBankAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id
            from accounts
            where company_id = @company_id
              and id = @account_id
              and is_active = true
              and root_type = 'asset'
              and detail_type = 'bank'
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("account_id", bankAccountId);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            throw new InvalidOperationException(
                "Pay bill draft references a bank account outside the active company context or an inactive bank account.");
        }
    }

    private static async Task<string> LoadCompanyBaseCurrencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
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
        command.Parameters.AddWithValue("company_id", companyId);
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
        string paymentNumber,
        PayBillDraftPreparation preparation,
        decimal totalAmount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into pay_bills (
              id,
              company_id,
              entity_number,
              payment_number,
              vendor_id,
              status,
              payment_date,
              bank_account_id,
              document_currency_code,
              base_currency_code,
              fx_rate_snapshot_id,
              fx_rate,
              fx_requested_date,
              fx_effective_date,
              fx_source,
              total_amount,
              memo,
              created_by_user_id
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @payment_number,
              @vendor_id,
              'draft',
              @payment_date,
              @bank_account_id,
              @document_currency_code,
              @base_currency_code,
              @fx_rate_snapshot_id,
              @fx_rate,
              @fx_requested_date,
              @fx_effective_date,
              @fx_source,
              @total_amount,
              @memo,
              @created_by_user_id
            );
            """;
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", preparation.Context.CompanyId);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("payment_number", paymentNumber);
        command.Parameters.AddWithValue("vendor_id", preparation.Context.VendorId);
        command.Parameters.AddWithValue("payment_date", preparation.Context.PaymentDate);
        command.Parameters.AddWithValue("bank_account_id", preparation.Context.BankAccountId);
        command.Parameters.AddWithValue("document_currency_code", preparation.DocumentCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", preparation.BaseCurrencyCode);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("fx_rate_snapshot_id", NpgsqlDbType.Uuid)
        {
            TypedValue = preparation.FxResolution.SnapshotId
        });
        command.Parameters.AddWithValue("fx_rate", preparation.FxResolution.Rate);
        command.Parameters.AddWithValue("fx_requested_date", preparation.FxResolution.RequestedDate);
        command.Parameters.AddWithValue("fx_effective_date", preparation.FxResolution.EffectiveDate);
        command.Parameters.AddWithValue("fx_source", preparation.FxResolution.SourceSemantics);
        command.Parameters.AddWithValue("total_amount", totalAmount);
        command.Parameters.AddWithValue("memo",
            string.IsNullOrWhiteSpace(preparation.Context.Memo) ? (object)DBNull.Value : preparation.Context.Memo.Trim());
        command.Parameters.AddWithValue("created_by_user_id", preparation.Context.UserId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDraftLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        PayBillDraftPreparation preparation,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < preparation.Lines.Count; index++)
        {
            var line = preparation.Lines[index];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                insert into pay_bill_lines (
                  company_id,
                  pay_bill_id,
                  line_number,
                  target_ap_open_item_id,
                  applied_amount_tx
                )
                values (
                  @company_id,
                  @pay_bill_id,
                  @line_number,
                  @target_ap_open_item_id,
                  @applied_amount_tx
                );
                """;
            command.Parameters.AddWithValue("company_id", preparation.Context.CompanyId);
            command.Parameters.AddWithValue("pay_bill_id", documentId);
            command.Parameters.AddWithValue("line_number", index + 1);
            command.Parameters.AddWithValue("target_ap_open_item_id", line.TargetOpenItemId);
            command.Parameters.AddWithValue("applied_amount_tx", line.AppliedAmountTx);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<long> FindPaymentSeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select coalesce(
              max(
                case
                  when payment_number ~ '^PB-[0-9]+$'
                    then substring(payment_number from 4)::bigint
                  else null
                end
              ),
              0
            ) + 1
            from pay_bills
            where company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
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
}
