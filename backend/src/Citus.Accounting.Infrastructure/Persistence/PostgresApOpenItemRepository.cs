using Citus.Accounting.Application.Repositories;
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
