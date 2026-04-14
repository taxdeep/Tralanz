using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresApOpenItemRepository : IApOpenItemRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresApOpenItemRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task EnsureForBillAsync(
        BillDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        await EnsureOpenItemAsync(
            companyId: document.CompanyId.Value,
            partyId: document.PartyId,
            sourceType: "bill",
            sourceId: document.Id,
            documentCurrencyCode: document.TransactionCurrencyCode.Value,
            baseCurrencyCode: document.BaseCurrencyCode.Value,
            originalAmountTx: document.TotalAmount,
            originalAmountBase: originalAmountBase,
            dueDate: document.DueDate,
            balanceSide: "credit",
            cancellationToken: cancellationToken);
    }

    public async Task EnsureForVendorCreditAsync(
        VendorCreditDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        await EnsureOpenItemAsync(
            companyId: document.CompanyId.Value,
            partyId: document.PartyId,
            sourceType: "vendor_credit",
            sourceId: document.Id,
            documentCurrencyCode: document.TransactionCurrencyCode.Value,
            baseCurrencyCode: document.BaseCurrencyCode.Value,
            originalAmountTx: document.TotalAmount,
            originalAmountBase: originalAmountBase,
            dueDate: document.DueDate,
            balanceSide: "debit",
            cancellationToken: cancellationToken);
    }

    public async Task<OpenItemDrillDown?> GetDrillDownAsync(
        CompanyId companyId,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            """
            select
              oi.id as open_item_id,
              oi.vendor_id as party_id,
              v.entity_number as party_entity_number,
              v.display_name as party_display_name,
              oi.source_type,
              oi.source_id as source_document_id,
              coalesce(b.bill_number, vc.vendor_credit_number, oi.source_id::text) as source_document_display_number,
              coalesce(b.bill_date, vc.vendor_credit_date, oi.due_date) as document_date,
              oi.due_date,
              oi.document_currency_code,
              oi.base_currency_code,
              oi.balance_side,
              oi.status,
              oi.original_amount_tx,
              oi.original_amount_base,
              oi.open_amount_tx,
              oi.open_amount_base
            from ap_open_items oi
            inner join vendors v
              on v.company_id = oi.company_id
             and v.id = oi.vendor_id
            left join bills b
              on oi.source_type = 'bill'
             and b.company_id = oi.company_id
             and b.id = oi.source_id
            left join vendor_credits vc
              on oi.source_type = 'vendor_credit'
             and vc.company_id = oi.company_id
             and vc.id = oi.source_id
            where oi.company_id = @company_id
              and oi.id = @open_item_id
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("open_item_id", openItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OpenItemDrillDown(
            reader.GetGuid(reader.GetOrdinal("open_item_id")),
            "ap",
            companyId,
            "vendor",
            reader.GetGuid(reader.GetOrdinal("party_id")),
            reader.GetString(reader.GetOrdinal("party_entity_number")),
            reader.GetString(reader.GetOrdinal("party_display_name")),
            reader.GetString(reader.GetOrdinal("source_type")),
            reader.GetGuid(reader.GetOrdinal("source_document_id")),
            reader.GetString(reader.GetOrdinal("source_document_display_number")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
            reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date")),
            reader.GetString(reader.GetOrdinal("document_currency_code")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetString(reader.GetOrdinal("balance_side")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetDecimal(reader.GetOrdinal("original_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("original_amount_base")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_base")));
    }

    private async Task EnsureOpenItemAsync(
        Guid companyId,
        Guid partyId,
        string sourceType,
        Guid sourceId,
        string documentCurrencyCode,
        string baseCurrencyCode,
        decimal originalAmountTx,
        decimal originalAmountBase,
        DateOnly? dueDate,
        string balanceSide,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using (var existingCommand = scope.CreateCommand(
                         """
                         select id
                         from ap_open_items
                         where company_id = @company_id
                           and source_type = @source_type
                           and source_id = @source_id
                         limit 1;
                         """))
        {
            existingCommand.Parameters.AddWithValue("company_id", companyId);
            existingCommand.Parameters.AddWithValue("source_type", sourceType);
            existingCommand.Parameters.AddWithValue("source_id", sourceId);

            var existing = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is not null && existing != DBNull.Value)
            {
                return;
            }
        }

        await using var command = scope.CreateCommand(
            """
            insert into ap_open_items (
              id,
              company_id,
              vendor_id,
              source_type,
              source_id,
              balance_side,
              document_currency_code,
              base_currency_code,
              original_amount_tx,
              original_amount_base,
              open_amount_tx,
              open_amount_base,
              status,
              due_date,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @vendor_id,
              @source_type,
              @source_id,
              @balance_side,
              @document_currency_code,
              @base_currency_code,
              @original_amount_tx,
              @original_amount_base,
              @open_amount_tx,
              @open_amount_base,
              'open',
              @due_date,
              now(),
              now()
            );
            """);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("vendor_id", partyId);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("balance_side", balanceSide);
        command.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("original_amount_tx", originalAmountTx);
        command.Parameters.AddWithValue("original_amount_base", originalAmountBase);
        command.Parameters.AddWithValue("open_amount_tx", originalAmountTx);
        command.Parameters.AddWithValue("open_amount_base", originalAmountBase);
        command.Parameters.AddWithValue(
            "due_date",
            dueDate.HasValue ? (object)dueDate.Value : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
