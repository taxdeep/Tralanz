using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// Bank deposit repository — header + items table.
///
/// The Undeposited-Funds account that the items are sitting on is NOT
/// stored on each deposit row; we resolve it once per
/// <see cref="GetForPostingAsync"/> by looking up the canonical
/// 'Undeposited Funds' account in the company chart. V1 expects code
/// '12000' (QBO standard); a future iteration adds an explicit
/// company_settings.undeposited_funds_account_id column when the
/// settings page lands.
/// </summary>
public sealed class PostgresBankDepositDocumentRepository : IBankDepositDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresBankDepositDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<BankDepositDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        Guid id;
        string entityNumber;
        string depositNumber;
        string status;
        DateOnly depositDate;
        Guid depositToAccountId;
        string documentCurrencyCode;
        decimal totalAmount;
        string? referenceNo;
        string? memo;

        await using (var headerCommand = scope.CreateCommand(
                         """
                         select
                           bd.id,
                           bd.entity_number,
                           bd.deposit_number,
                           bd.status,
                           bd.deposit_date,
                           bd.deposit_to_account_id,
                           bd.document_currency_code,
                           bd.total_amount,
                           bd.reference_no,
                           bd.memo
                         from bank_deposits bd
                         where bd.company_id = @company_id
                           and bd.id = @document_id
                         limit 1;
                         """))
        {
            headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            headerCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            depositNumber = reader.GetString(reader.GetOrdinal("deposit_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            depositDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("deposit_date"));
            depositToAccountId = reader.GetGuid(reader.GetOrdinal("deposit_to_account_id"));
            documentCurrencyCode = reader.GetString(reader.GetOrdinal("document_currency_code"));
            totalAmount = reader.GetDecimal(reader.GetOrdinal("total_amount"));
            referenceNo = reader.IsDBNull(reader.GetOrdinal("reference_no"))
                ? null
                : reader.GetString(reader.GetOrdinal("reference_no"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
        }

        // Resolve the canonical Undeposited Funds account by code.
        // V1 hard-codes '12000' (QBO standard); future iteration
        // promotes this to company_settings.
        Guid undepositedFundsAccountId;
        await using (var undepositedCommand = scope.CreateCommand(
                         """
                         select id
                         from accounts
                         where company_id = @company_id
                           and code = '12000'
                           and is_active = true
                         limit 1;
                         """))
        {
            undepositedCommand.Parameters.AddWithValue("company_id", companyId.Value);
            var result = await undepositedCommand.ExecuteScalarAsync(cancellationToken);
            if (result is null || result is DBNull)
            {
                throw new InvalidOperationException(
                    "Bank deposit posting requires an active 'Undeposited Funds' account (code 12000) on the chart of accounts.");
            }
            undepositedFundsAccountId = (Guid)result;
        }

        var items = new List<BankDepositItemDocumentLine>();
        await using (var itemsCommand = scope.CreateCommand(
                         """
                         select
                           item.line_number,
                           item.source_item_kind,
                           item.source_item_id,
                           item.source_item_display_number,
                           item.payer_name,
                           item.payment_method,
                           item.reference_no,
                           item.amount
                         from bank_deposit_items item
                         where item.company_id = @company_id
                           and item.bank_deposit_id = @document_id
                         order by item.line_number asc;
                         """))
        {
            itemsCommand.Parameters.AddWithValue("company_id", companyId.Value);
            itemsCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await itemsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var lineNumber = reader.GetInt32(reader.GetOrdinal("line_number"));
                var sourceItemKind = reader.GetString(reader.GetOrdinal("source_item_kind"));
                Guid? sourceItemId = reader.IsDBNull(reader.GetOrdinal("source_item_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("source_item_id"));
                var sourceItemDisplayNumber = reader.GetString(reader.GetOrdinal("source_item_display_number"));
                var payerName = reader.IsDBNull(reader.GetOrdinal("payer_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("payer_name"));
                var paymentMethod = reader.IsDBNull(reader.GetOrdinal("payment_method"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("payment_method"));
                var itemRef = reader.IsDBNull(reader.GetOrdinal("reference_no"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("reference_no"));
                var amount = reader.GetDecimal(reader.GetOrdinal("amount"));

                items.Add(new BankDepositItemDocumentLine(
                    lineNumber,
                    sourceItemKind,
                    sourceItemId,
                    sourceItemDisplayNumber,
                    payerName,
                    paymentMethod,
                    itemRef,
                    amount));
            }
        }

        return new BankDepositDocument(
            id,
            companyId,
            new EntityNumber(entityNumber),
            new DocumentNumber(depositNumber),
            status,
            depositDate,
            depositToAccountId,
            undepositedFundsAccountId,
            new CurrencyCode(documentCurrencyCode),
            new CurrencyCode(documentCurrencyCode),
            totalAmount,
            referenceNo,
            memo,
            items);
    }

    public async Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        BankDepositDraftSaveModel draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var documentId = draft.DocumentId ?? Guid.NewGuid();
        string entityNumber;
        string depositNumber;

        if (draft.DocumentId is null)
        {
            var year = draft.DepositDate.Year;
            entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId.Value,
                $"entity-number:all:{year}",
                $"EN{year}",
                8,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(
                    connection, transaction, year, cancellationToken),
                cancellationToken);

            depositNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId.Value,
                "bank-deposit-display",
                "BD-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId.Value,
                    "bank_deposits",
                    "deposit_number",
                    "^BD-[0-9]+$",
                    4,
                    cancellationToken),
                cancellationToken);

            var total = Round6(draft.Items.Sum(i => i.Amount));

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into bank_deposits (
                  id,
                  company_id,
                  entity_number,
                  deposit_number,
                  status,
                  deposit_date,
                  deposit_to_account_id,
                  document_currency_code,
                  total_amount,
                  reference_no,
                  memo,
                  posted_at,
                  created_by_user_id,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @deposit_number,
                  'draft',
                  @deposit_date,
                  @deposit_to_account_id,
                  @document_currency_code,
                  @total_amount,
                  @reference_no,
                  @memo,
                  null,
                  @created_by_user_id,
                  now(),
                  now()
                );
                """;
            BindHeader(insertCommand, draft, documentId, entityNumber, depositNumber, total);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            (entityNumber, depositNumber) = await LoadIdentityAsync(
                connection, transaction, draft.CompanyId.Value, documentId, cancellationToken);

            var total = Round6(draft.Items.Sum(i => i.Amount));

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update bank_deposits
                set deposit_date = @deposit_date,
                    deposit_to_account_id = @deposit_to_account_id,
                    document_currency_code = @document_currency_code,
                    total_amount = @total_amount,
                    reference_no = @reference_no,
                    memo = @memo,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = 'draft';
                """;
            BindHeader(updateCommand, draft, documentId, entityNumber, depositNumber, total, includeIdentity: false);
            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "The bank deposit draft could not be updated. Only draft deposits can be modified.");
            }
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                delete from bank_deposit_items
                where company_id = @company_id
                  and bank_deposit_id = @bank_deposit_id;
                """;
            deleteCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            deleteCommand.Parameters.AddWithValue("bank_deposit_id", documentId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in draft.Items.OrderBy(static i => i.LineNumber))
        {
            await using var insertItemCommand = connection.CreateCommand();
            insertItemCommand.Transaction = transaction;
            insertItemCommand.CommandText =
                """
                insert into bank_deposit_items (
                  id,
                  company_id,
                  bank_deposit_id,
                  line_number,
                  source_item_kind,
                  source_item_id,
                  source_item_display_number,
                  payer_name,
                  payment_method,
                  reference_no,
                  amount,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @bank_deposit_id,
                  @line_number,
                  @source_item_kind,
                  @source_item_id,
                  @source_item_display_number,
                  @payer_name,
                  @payment_method,
                  @reference_no,
                  @amount,
                  now(),
                  now()
                );
                """;
            insertItemCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertItemCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            insertItemCommand.Parameters.AddWithValue("bank_deposit_id", documentId);
            insertItemCommand.Parameters.AddWithValue("line_number", item.LineNumber);
            insertItemCommand.Parameters.AddWithValue("source_item_kind", string.IsNullOrWhiteSpace(item.SourceItemKind) ? "manual" : item.SourceItemKind.Trim().ToLowerInvariant());
            insertItemCommand.Parameters.Add(new NpgsqlParameter<Guid?>("source_item_id", NpgsqlDbType.Uuid) { TypedValue = item.SourceItemId });
            insertItemCommand.Parameters.AddWithValue("source_item_display_number", item.SourceItemDisplayNumber.Trim());
            insertItemCommand.Parameters.AddWithValue("payer_name", string.IsNullOrWhiteSpace(item.PayerName) ? (object)DBNull.Value : item.PayerName.Trim());
            insertItemCommand.Parameters.AddWithValue("payment_method", string.IsNullOrWhiteSpace(item.PaymentMethod) ? (object)DBNull.Value : item.PaymentMethod.Trim().ToLowerInvariant());
            insertItemCommand.Parameters.AddWithValue("reference_no", string.IsNullOrWhiteSpace(item.ReferenceNo) ? (object)DBNull.Value : item.ReferenceNo.Trim());
            insertItemCommand.Parameters.AddWithValue("amount", Round6(item.Amount));
            await insertItemCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, depositNumber, "draft");
    }

    private static void ValidateDraft(BankDepositDraftSaveModel draft)
    {
        if (draft.DepositToAccountId == Guid.Empty)
        {
            throw new InvalidOperationException("Bank deposit requires a deposit-to account id.");
        }
        if (draft.Items is null || draft.Items.Count == 0)
        {
            throw new InvalidOperationException("Bank deposit must include at least one item.");
        }
        if (string.IsNullOrWhiteSpace(draft.TransactionCurrencyCode))
        {
            throw new InvalidOperationException("Bank deposit requires a transaction currency code.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var item in draft.Items)
        {
            if (item.LineNumber <= 0)
            {
                throw new InvalidOperationException("Bank deposit item line numbers must be positive.");
            }
            if (!seenLineNumbers.Add(item.LineNumber))
            {
                throw new InvalidOperationException($"Bank deposit item line numbers must be unique; duplicate {item.LineNumber}.");
            }
            if (string.IsNullOrWhiteSpace(item.SourceItemDisplayNumber))
            {
                throw new InvalidOperationException($"Bank deposit item {item.LineNumber} requires a source display number.");
            }
            if (item.Amount <= 0m)
            {
                throw new InvalidOperationException($"Bank deposit item {item.LineNumber} amount must be positive.");
            }
        }
    }

    private static void BindHeader(
        NpgsqlCommand command,
        BankDepositDraftSaveModel draft,
        Guid documentId,
        string entityNumber,
        string depositNumber,
        decimal total,
        bool includeIdentity = true)
    {
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("deposit_number", depositNumber);
            command.Parameters.AddWithValue("created_by_user_id", draft.UserId.Value);
        }

        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
        command.Parameters.AddWithValue("deposit_date", draft.DepositDate);
        command.Parameters.AddWithValue("deposit_to_account_id", draft.DepositToAccountId);
        command.Parameters.AddWithValue("document_currency_code", draft.TransactionCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("total_amount", total);
        command.Parameters.AddWithValue("reference_no", string.IsNullOrWhiteSpace(draft.ReferenceNo) ? (object)DBNull.Value : draft.ReferenceNo.Trim());
        command.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(draft.Memo) ? (object)DBNull.Value : draft.Memo.Trim());
    }

    private static async Task<(string EntityNumber, string DepositNumber)> LoadIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select entity_number, deposit_number
            from bank_deposits
            where id = @id and company_id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Bank deposit draft not found in the active company context.");
        }

        return (
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("deposit_number")));
    }

    private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.ToEven);
}
