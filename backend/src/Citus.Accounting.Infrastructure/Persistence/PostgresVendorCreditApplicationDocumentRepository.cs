using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Infrastructure;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresVendorCreditApplicationDocumentRepository : IVendorCreditApplicationDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresVendorCreditApplicationDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<VendorCreditApplicationDocument?> GetForPostingAsync(
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
        Guid vendorId;
        Guid payableAccountId;
        string documentCurrencyCode;
        string baseCurrencyCode;
        decimal totalAmount;
        string? memo;

        await using (var headerCommand = scope.CreateCommand(
                         """
                         select
                           vca.id,
                           vca.entity_number,
                           vca.application_number,
                           vca.status,
                           vca.application_date,
                           vca.vendor_id,
                           vca.document_currency_code,
                           vca.base_currency_code,
                           vca.total_amount,
                           vca.memo,
                           (
                             select a.id
                             from accounts a
                             where a.company_id = vca.company_id
                               and a.is_active = true
                               and (
                                 (vca.document_currency_code = vca.base_currency_code and (a.system_role = 'accounts_payable' or a.code = '2000'))
                                 or
                                 (vca.document_currency_code <> vca.base_currency_code and (a.system_role = ('accounts_payable:' || vca.document_currency_code) or a.code = ('AP-' || vca.document_currency_code)))
                               )
                             order by
                               case
                                 when vca.document_currency_code = vca.base_currency_code and a.system_role = 'accounts_payable' then 0
                                 when vca.document_currency_code <> vca.base_currency_code and a.system_role = ('accounts_payable:' || vca.document_currency_code) then 0
                                 else 1
                               end,
                               a.code
                             limit 1
                           ) as payable_account_id
                         from vendor_credit_applications vca
                         where vca.company_id = @company_id
                           and vca.id = @document_id
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
            vendorId = reader.GetGuid(reader.GetOrdinal("vendor_id"));
            payableAccountId = reader.IsDBNull(reader.GetOrdinal("payable_account_id"))
                ? Guid.Empty
                : reader.GetGuid(reader.GetOrdinal("payable_account_id"));
            documentCurrencyCode = reader.GetString(reader.GetOrdinal("document_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            totalAmount = reader.GetDecimal(reader.GetOrdinal("total_amount"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
        }

        if (payableAccountId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Vendor credit application routing could not resolve an active Accounts Payable control account.");
        }

        var rawLines = new List<(int LineNumber, Guid SourceVendorCreditApOpenItemId, Guid TargetBillApOpenItemId, decimal AppliedAmountTx)>();
        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.source_vendor_credit_ap_open_item_id,
                           l.target_bill_ap_open_item_id,
                           l.applied_amount_tx
                         from vendor_credit_application_lines l
                         where l.company_id = @company_id
                           and l.vendor_credit_application_id = @document_id
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
                    reader.GetGuid(reader.GetOrdinal("source_vendor_credit_ap_open_item_id")),
                    reader.GetGuid(reader.GetOrdinal("target_bill_ap_open_item_id")),
                    reader.GetDecimal(reader.GetOrdinal("applied_amount_tx"))));
            }
        }

        var appliedTotal = rawLines.Sum(static line => line.AppliedAmountTx);
        if (appliedTotal != totalAmount)
        {
            throw new InvalidOperationException("Vendor credit application total must equal the sum of its application lines.");
        }

        var lines = new List<VendorCreditApplicationDocumentLine>();
        foreach (var rawLine in rawLines)
        {
            var source = await LoadApOpenItemAsync(scope, companyId.Value, rawLine.SourceVendorCreditApOpenItemId, cancellationToken);
            var target = await LoadApOpenItemAsync(scope, companyId.Value, rawLine.TargetBillApOpenItemId, cancellationToken);

            if (source.VendorId != vendorId || target.VendorId != vendorId)
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

            if (!string.Equals(source.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Vendor credit application lines must use the same transaction currency as the application document.");
            }

            if (source.Status is not ("open" or "partially_applied") || target.Status is not ("open" or "partially_applied"))
            {
                throw new InvalidOperationException("Vendor credit application lines may only target open AP items.");
            }

            if (rawLine.AppliedAmountTx > source.OpenAmountTx || rawLine.AppliedAmountTx > target.OpenAmountTx)
            {
                throw new InvalidOperationException("Vendor credit application line exceeds the current open amount.");
            }

            var sourceCarryingAmountBase = SettlementAmountMath.CalculateCarryingAmountBase(
                rawLine.AppliedAmountTx,
                source.OpenAmountTx,
                source.OpenAmountBase);
            var targetCarryingAmountBase = SettlementAmountMath.CalculateCarryingAmountBase(
                rawLine.AppliedAmountTx,
                target.OpenAmountTx,
                target.OpenAmountBase);

            lines.Add(new VendorCreditApplicationDocumentLine(
                rawLine.LineNumber,
                rawLine.SourceVendorCreditApOpenItemId,
                rawLine.TargetBillApOpenItemId,
                $"Vendor credit application line {rawLine.LineNumber}",
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
                companyId.Value,
                cancellationToken,
                "realized_fx_gain",
                "fx_gain_realized");
            realizedFxLossAccountId = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
                scope,
                companyId.Value,
                cancellationToken,
                "realized_fx_loss",
                "fx_loss_realized");

            if (!realizedFxGainAccountId.HasValue || !realizedFxLossAccountId.HasValue)
            {
                throw new InvalidOperationException(
                    "Vendor credit application routing could not resolve active realized FX gain/loss accounts. Configure accounts.system_role or accounts.system_key with 'realized_fx_gain' and 'realized_fx_loss'.");
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

        return new VendorCreditApplicationDocument(
            id,
            companyId,
            new EntityNumber(entityNumber),
            new DocumentNumber(applicationNumber),
            status,
            applicationDate,
            vendorId,
            payableAccountId,
            realizedFxGainAccountId,
            realizedFxLossAccountId,
            transactionCurrency,
            baseCurrency,
            fxSnapshot,
            lines,
            totalAmount,
            memo);
    }

    private static async Task<ApOpenItemTarget> LoadApOpenItemAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              vendor_id,
              source_type,
              balance_side,
              document_currency_code,
              status,
              open_amount_tx,
              open_amount_base
            from ap_open_items
            where company_id = @company_id
              and id = @open_item_id
            for update;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("open_item_id", openItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Vendor credit application line references an AP open item that does not exist.");
        }

        return new ApOpenItemTarget(
            reader.GetGuid(reader.GetOrdinal("vendor_id")),
            reader.GetString(reader.GetOrdinal("source_type")),
            reader.GetString(reader.GetOrdinal("balance_side")),
            reader.GetString(reader.GetOrdinal("document_currency_code")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_base")));
    }

    private sealed record ApOpenItemTarget(
        Guid VendorId,
        string SourceType,
        string BalanceSide,
        string DocumentCurrencyCode,
        string Status,
        decimal OpenAmountTx,
        decimal OpenAmountBase);
}
