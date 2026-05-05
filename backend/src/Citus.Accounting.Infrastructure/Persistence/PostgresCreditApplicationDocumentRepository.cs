using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Infrastructure;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresCreditApplicationDocumentRepository : ICreditApplicationDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresCreditApplicationDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<CreditApplicationDocument?> GetForPostingAsync(
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
        string applicationNumber;
        string status;
        DateOnly applicationDate;
        Guid customerId;
        Guid receivableAccountId;
        string documentCurrencyCode;
        string baseCurrencyCode;
        decimal totalAmount;
        string? memo;

        await using (var headerCommand = scope.CreateCommand(
                         """
                         select
                           ca.id,
                           ca.entity_number,
                           ca.application_number,
                           ca.status,
                           ca.application_date,
                           ca.customer_id,
                           ca.document_currency_code,
                           ca.base_currency_code,
                           ca.total_amount,
                           ca.memo,
                           (
                             select a.id
                             from accounts a
                             where a.company_id = ca.company_id
                               and a.is_active = true
                               and (
                                 (ca.document_currency_code = ca.base_currency_code and (a.system_role = 'accounts_receivable' or a.code = '1100'))
                                 or
                                 (ca.document_currency_code <> ca.base_currency_code and (a.system_role = ('accounts_receivable:' || ca.document_currency_code) or a.code = ('AR-' || ca.document_currency_code)))
                               )
                             order by
                               case
                                 when ca.document_currency_code = ca.base_currency_code and a.system_role = 'accounts_receivable' then 0
                                 when ca.document_currency_code <> ca.base_currency_code and a.system_role = ('accounts_receivable:' || ca.document_currency_code) then 0
                                 else 1
                               end,
                               a.code
                             limit 1
                           ) as receivable_account_id
                         from credit_applications ca
                         where ca.company_id = @company_id
                           and ca.id = @document_id
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
            applicationNumber = reader.GetString(reader.GetOrdinal("application_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            applicationDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("application_date"));
            customerId = reader.GetGuid(reader.GetOrdinal("customer_id"));
            receivableAccountId = reader.IsDBNull(reader.GetOrdinal("receivable_account_id"))
                ? Guid.Empty
                : reader.GetGuid(reader.GetOrdinal("receivable_account_id"));
            documentCurrencyCode = reader.GetString(reader.GetOrdinal("document_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            totalAmount = reader.GetDecimal(reader.GetOrdinal("total_amount"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
        }

        if (receivableAccountId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Credit application routing could not resolve an active Accounts Receivable control account.");
        }

        var rawLines = new List<(int LineNumber, Guid SourceCreditArOpenItemId, Guid TargetInvoiceArOpenItemId, decimal AppliedAmountTx)>();
        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.source_credit_ar_open_item_id,
                           l.target_invoice_ar_open_item_id,
                           l.applied_amount_tx
                         from credit_application_lines l
                         where l.company_id = @company_id
                           and l.credit_application_id = @document_id
                         order by l.line_number asc;
                         """))
        {
            linesCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linesCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rawLines.Add((
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("source_credit_ar_open_item_id")),
                    reader.GetGuid(reader.GetOrdinal("target_invoice_ar_open_item_id")),
                    reader.GetDecimal(reader.GetOrdinal("applied_amount_tx"))));
            }
        }

        var appliedTotal = rawLines.Sum(static line => line.AppliedAmountTx);
        if (appliedTotal != totalAmount)
        {
            throw new InvalidOperationException("Credit application total must equal the sum of its application lines.");
        }

        var lines = new List<CreditApplicationDocumentLine>();
        foreach (var rawLine in rawLines)
        {
            var source = await LoadArOpenItemAsync(scope, companyId, rawLine.SourceCreditArOpenItemId, cancellationToken);
            var target = await LoadArOpenItemAsync(scope, companyId, rawLine.TargetInvoiceArOpenItemId, cancellationToken);

            if (source.CustomerId != customerId || target.CustomerId != customerId)
            {
                throw new InvalidOperationException("Credit application lines must target open items from the same customer.");
            }

            if (source.SourceType != "credit_note" || source.BalanceSide != "credit")
            {
                throw new InvalidOperationException("Credit application source lines must reference open credit-note AR items.");
            }

            if (target.SourceType != "invoice" || target.BalanceSide != "debit")
            {
                throw new InvalidOperationException("Credit application target lines must reference open invoice AR items.");
            }

            if (!string.Equals(source.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Credit application lines must use the same transaction currency as the application document.");
            }

            if (source.Status is not ("open" or "partially_applied") || target.Status is not ("open" or "partially_applied"))
            {
                throw new InvalidOperationException("Credit application lines may only target open AR items.");
            }

            if (rawLine.AppliedAmountTx > source.OpenAmountTx || rawLine.AppliedAmountTx > target.OpenAmountTx)
            {
                throw new InvalidOperationException("Credit application line exceeds the current open amount.");
            }

            var sourceCarryingAmountBase = SettlementAmountMath.CalculateCarryingAmountBase(
                rawLine.AppliedAmountTx,
                source.OpenAmountTx,
                source.OpenAmountBase);
            var targetCarryingAmountBase = SettlementAmountMath.CalculateCarryingAmountBase(
                rawLine.AppliedAmountTx,
                target.OpenAmountTx,
                target.OpenAmountBase);

            lines.Add(new CreditApplicationDocumentLine(
                rawLine.LineNumber,
                rawLine.SourceCreditArOpenItemId,
                rawLine.TargetInvoiceArOpenItemId,
                $"Credit application line {rawLine.LineNumber}",
                rawLine.AppliedAmountTx,
                sourceCarryingAmountBase,
                targetCarryingAmountBase));
        }

        Guid? realizedFxGainAccountId = null;
        Guid? realizedFxLossAccountId = null;
        var transactionCurrency = new CurrencyCode(documentCurrencyCode);
        var baseCurrency = new CurrencyCode(baseCurrencyCode);

        if (transactionCurrency != baseCurrency &&
            lines.Any(static line => line.RealizedFxAmountBase != 0m))
        {
            realizedFxGainAccountId = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
                scope,
                companyId,
                cancellationToken,
                "realized_fx_gain",
                "fx_gain_realized");
            realizedFxLossAccountId = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
                scope,
                companyId,
                cancellationToken,
                "realized_fx_loss",
                "fx_loss_realized");

            if (!realizedFxGainAccountId.HasValue || !realizedFxLossAccountId.HasValue)
            {
                throw new InvalidOperationException(
                    "Credit application routing could not resolve active realized FX gain/loss accounts. Configure accounts.system_role or accounts.system_key with 'realized_fx_gain' and 'realized_fx_loss'.");
            }
        }

        FxSnapshotRef? fxSnapshot = null;
        if (transactionCurrency != baseCurrency)
        {
            fxSnapshot = new FxSnapshotRef(
                Guid.Empty,
                baseCurrency,
                transactionCurrency,
                1m,
                applicationDate,
                applicationDate,
                "subledger_carrying");
        }

        return new CreditApplicationDocument(
            id,
            companyId,
            new EntityNumber(entityNumber),
            new DocumentNumber(applicationNumber),
            status,
            applicationDate,
            customerId,
            receivableAccountId,
            realizedFxGainAccountId,
            realizedFxLossAccountId,
            transactionCurrency,
            baseCurrency,
            fxSnapshot,
            lines,
            totalAmount,
            memo);
    }

    private static async Task<ArOpenItemTarget> LoadArOpenItemAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              customer_id,
              source_type,
              balance_side,
              document_currency_code,
              status,
              open_amount_tx,
              open_amount_base
            from ar_open_items
            where company_id = @company_id
              and id = @open_item_id
            for update;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("open_item_id", openItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Credit application line references an AR open item that does not exist.");
        }

        return new ArOpenItemTarget(
            reader.GetGuid(reader.GetOrdinal("customer_id")),
            reader.GetString(reader.GetOrdinal("source_type")),
            reader.GetString(reader.GetOrdinal("balance_side")),
            reader.GetString(reader.GetOrdinal("document_currency_code")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_base")));
    }

    private sealed record ArOpenItemTarget(
        Guid CustomerId,
        string SourceType,
        string BalanceSide,
        string DocumentCurrencyCode,
        string Status,
        decimal OpenAmountTx,
        decimal OpenAmountBase);
}
