using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// Bank transfer repository — single-row document, no lines table.
/// Persistence is much simpler than the line-bearing documents:
/// just header CRUD on <c>bank_transfers</c>. The two-fragment
/// posting (Cr from / Dr to) is materialised at engine time via
/// <c>BuildBankTransferFragments</c>.
///
/// FX-rate polarity invariant (same-currency null vs. cross-currency
/// positive) is enforced both by the Domain constructor and by the
/// row-level CHECK on bank_transfers — defence in depth so a malformed
/// write never lands.
/// </summary>
public sealed class PostgresBankTransferDocumentRepository : IBankTransferDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresBankTransferDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<BankTransferDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            """
            select
              bt.id,
              bt.entity_number,
              bt.transfer_number,
              bt.status,
              bt.transfer_date,
              bt.from_account_id,
              bt.from_currency_code,
              bt.to_account_id,
              bt.to_currency_code,
              bt.amount,
              bt.fx_rate,
              bt.reference_no,
              bt.memo
            from bank_transfers bt
            where bt.company_id = @company_id
              and bt.id = @document_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var id = reader.GetGuid(reader.GetOrdinal("id"));
        var entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
        var transferNumber = reader.GetString(reader.GetOrdinal("transfer_number"));
        var status = reader.GetString(reader.GetOrdinal("status"));
        var transferDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("transfer_date"));
        var fromAccountId = reader.GetGuid(reader.GetOrdinal("from_account_id"));
        var fromCurrencyCode = reader.GetString(reader.GetOrdinal("from_currency_code"));
        var toAccountId = reader.GetGuid(reader.GetOrdinal("to_account_id"));
        var toCurrencyCode = reader.GetString(reader.GetOrdinal("to_currency_code"));
        var amount = reader.GetDecimal(reader.GetOrdinal("amount"));
        decimal? fxRate = reader.IsDBNull(reader.GetOrdinal("fx_rate"))
            ? null
            : reader.GetDecimal(reader.GetOrdinal("fx_rate"));
        var referenceNo = reader.IsDBNull(reader.GetOrdinal("reference_no"))
            ? null
            : reader.GetString(reader.GetOrdinal("reference_no"));
        var memo = reader.IsDBNull(reader.GetOrdinal("memo"))
            ? null
            : reader.GetString(reader.GetOrdinal("memo"));

        // Build an FxSnapshot only for cross-currency transfers; the
        // engine will resolve a real snapshot at post time using the
        // FromCurrency → ToCurrency pair. The stored fx_rate is the
        // bank's rate the operator captured; it travels alongside.
        FxSnapshotRef? fxSnapshot = null;
        if (fxRate.HasValue)
        {
            fxSnapshot = new FxSnapshotRef(
                Guid.Empty,
                new CurrencyCode(toCurrencyCode),
                new CurrencyCode(fromCurrencyCode),
                fxRate.Value,
                transferDate,
                transferDate,
                "operator");
        }

        return new BankTransferDocument(
            id,
            companyId,
            EntityNumber.Parse(entityNumber),
            new DocumentNumber(transferNumber),
            status,
            transferDate,
            fromAccountId,
            new CurrencyCode(fromCurrencyCode),
            toAccountId,
            new CurrencyCode(toCurrencyCode),
            amount,
            fxRate,
            fxSnapshot,
            referenceNo,
            memo);
    }

    public async Task<IReadOnlyList<BankTransferListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(includeDrafts
            ? """
              select bt.id, bt.entity_number, bt.transfer_number, bt.status, bt.transfer_date,
                     bt.from_account_id, bt.from_currency_code, bt.to_account_id, bt.to_currency_code,
                     bt.amount, bt.fx_rate, bt.posted_at
              from bank_transfers bt
              where bt.company_id = @company_id
              order by bt.transfer_date desc, bt.created_at desc
              limit 200;
              """
            : """
              select bt.id, bt.entity_number, bt.transfer_number, bt.status, bt.transfer_date,
                     bt.from_account_id, bt.from_currency_code, bt.to_account_id, bt.to_currency_code,
                     bt.amount, bt.fx_rate, bt.posted_at
              from bank_transfers bt
              where bt.company_id = @company_id
                and bt.status <> 'draft'
              order by bt.transfer_date desc, bt.created_at desc
              limit 200;
              """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var rows = new List<BankTransferListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BankTransferListItem(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("transfer_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("transfer_date")),
                reader.GetGuid(reader.GetOrdinal("from_account_id")),
                reader.GetString(reader.GetOrdinal("from_currency_code")),
                reader.GetGuid(reader.GetOrdinal("to_account_id")),
                reader.GetString(reader.GetOrdinal("to_currency_code")),
                reader.GetDecimal(reader.GetOrdinal("amount")),
                reader.IsDBNull(reader.GetOrdinal("fx_rate")) ? null : reader.GetDecimal(reader.GetOrdinal("fx_rate")),
                reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"))));
        }
        return rows;
    }

    public async Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        BankTransferDraftSaveModel draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var documentId = draft.DocumentId ?? Guid.NewGuid();
        string entityNumber;
        string transferNumber;

        if (draft.DocumentId is null)
        {
            var year = draft.TransferDate.Year;
            entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                $"entity-number:all:{year}",
                $"EN{year}",
                5,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(
                    connection, transaction, year, cancellationToken),
                cancellationToken);

            transferNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                "bank-transfer-display",
                "BT-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId,
                    "bank_transfers",
                    "transfer_number",
                    "^BT-[0-9]+$",
                    4,
                    cancellationToken),
                cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into bank_transfers (
                  id,
                  company_id,
                  entity_number,
                  transfer_number,
                  status,
                  transfer_date,
                  from_account_id,
                  from_currency_code,
                  to_account_id,
                  to_currency_code,
                  amount,
                  fx_rate,
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
                  @transfer_number,
                  'draft',
                  @transfer_date,
                  @from_account_id,
                  @from_currency_code,
                  @to_account_id,
                  @to_currency_code,
                  @amount,
                  @fx_rate,
                  @reference_no,
                  @memo,
                  null,
                  @created_by_user_id,
                  now(),
                  now()
                );
                """;
            BindHeader(insertCommand, draft, documentId, entityNumber, transferNumber);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            (entityNumber, transferNumber) = await LoadIdentityAsync(
                connection, transaction, draft.CompanyId, documentId, cancellationToken);

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update bank_transfers
                set transfer_date = @transfer_date,
                    from_account_id = @from_account_id,
                    from_currency_code = @from_currency_code,
                    to_account_id = @to_account_id,
                    to_currency_code = @to_currency_code,
                    amount = @amount,
                    fx_rate = @fx_rate,
                    reference_no = @reference_no,
                    memo = @memo,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = 'draft';
                """;
            BindHeader(updateCommand, draft, documentId, entityNumber, transferNumber, includeIdentity: false);
            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "The bank transfer draft could not be updated. Only draft transfers can be modified.");
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, transferNumber, "draft");
    }

    private static void ValidateDraft(BankTransferDraftSaveModel draft)
    {
        if (draft.FromAccountId == Guid.Empty)
        {
            throw new InvalidOperationException("Bank transfer requires a from-account id.");
        }
        if (draft.ToAccountId == Guid.Empty)
        {
            throw new InvalidOperationException("Bank transfer requires a to-account id.");
        }
        if (draft.FromAccountId == draft.ToAccountId)
        {
            throw new InvalidOperationException("Bank transfer source and destination accounts must differ.");
        }
        if (draft.Amount <= 0m)
        {
            throw new InvalidOperationException("Bank transfer amount must be positive.");
        }
        if (string.IsNullOrWhiteSpace(draft.FromCurrencyCode) || string.IsNullOrWhiteSpace(draft.ToCurrencyCode))
        {
            throw new InvalidOperationException("Bank transfer requires both source and destination currency codes.");
        }

        var sameCurrency = string.Equals(draft.FromCurrencyCode, draft.ToCurrencyCode, StringComparison.OrdinalIgnoreCase);
        if (sameCurrency && draft.FxRate is not null)
        {
            throw new InvalidOperationException("Same-currency bank transfers must carry no FX rate.");
        }
        if (!sameCurrency && (draft.FxRate is null || draft.FxRate.Value <= 0m))
        {
            throw new InvalidOperationException("Cross-currency bank transfers must carry a positive FX rate.");
        }
    }

    private static void BindHeader(
        NpgsqlCommand command,
        BankTransferDraftSaveModel draft,
        Guid documentId,
        string entityNumber,
        string transferNumber,
        bool includeIdentity = true)
    {
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("transfer_number", transferNumber);
            command.Parameters.AddWithValue("created_by_user_id", draft.UserId.Value);
        }

        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
        command.Parameters.AddWithValue("transfer_date", draft.TransferDate);
        command.Parameters.AddWithValue("from_account_id", draft.FromAccountId);
        command.Parameters.AddWithValue("from_currency_code", draft.FromCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("to_account_id", draft.ToAccountId);
        command.Parameters.AddWithValue("to_currency_code", draft.ToCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("amount", Round6(draft.Amount));
        command.Parameters.Add(new NpgsqlParameter<decimal?>("fx_rate", NpgsqlDbType.Numeric)
        {
            TypedValue = draft.FxRate.HasValue ? Math.Round(draft.FxRate.Value, 10) : null
        });
        command.Parameters.AddWithValue("reference_no", string.IsNullOrWhiteSpace(draft.ReferenceNo) ? (object)DBNull.Value : draft.ReferenceNo.Trim());
        command.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(draft.Memo) ? (object)DBNull.Value : draft.Memo.Trim());
    }

    private static async Task<(string EntityNumber, string TransferNumber)> LoadIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select entity_number, transfer_number
            from bank_transfers
            where id = @id and company_id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Bank transfer draft not found in the active company context.");
        }

        return (
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("transfer_number")));
    }

    private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.ToEven);
}
