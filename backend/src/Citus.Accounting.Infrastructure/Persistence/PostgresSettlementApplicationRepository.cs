using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Infrastructure;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresSettlementApplicationRepository : ISettlementApplicationRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresSettlementApplicationRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task ApplyReceivePaymentAsync(
        ReceivePaymentDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (await HasExistingApplicationsAsync(document.CompanyId, document.SourceType, document.Id, cancellationToken))
        {
            return;
        }

        foreach (var line in document.PaymentLines)
        {
            await ApplySingleAsync(
                companyId: document.CompanyId,
                sourceType: document.SourceType,
                sourceId: document.Id,
                applicationType: "receive_payment",
                targetOpenItemType: "ar_open_item",
                targetOpenItemId: line.TargetArOpenItemId,
                appliedAmountTx: line.AppliedAmount,
                appliedAmountBase: line.AppliedAmountBase,
                carryingAmountBase: line.CarryingAmountBase,
                documentCurrencyCode: document.TransactionCurrencyCode.Value,
                documentBaseCurrencyCode: document.BaseCurrencyCode.Value,
                settlementFxRate: document.TransactionCurrencyCode == document.BaseCurrencyCode
                    ? null
                    : document.FxSnapshot?.Rate,
                realizedFxAmount: document.TransactionCurrencyCode == document.BaseCurrencyCode
                    ? null
                    : SettlementAmountMath.RoundBase(line.AppliedAmountBase - line.CarryingAmountBase),
                partyId: document.PartyId,
                createdByUserId: createdByUserId,
                cancellationToken: cancellationToken);
        }
    }

    public async Task ApplyPayBillAsync(
        PayBillDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (await HasExistingApplicationsAsync(document.CompanyId, document.SourceType, document.Id, cancellationToken))
        {
            return;
        }

        foreach (var line in document.PaymentLines)
        {
            await ApplySingleAsync(
                companyId: document.CompanyId,
                sourceType: document.SourceType,
                sourceId: document.Id,
                applicationType: "pay_bill",
                targetOpenItemType: "ap_open_item",
                targetOpenItemId: line.TargetApOpenItemId,
                appliedAmountTx: line.AppliedAmount,
                appliedAmountBase: line.AppliedAmountBase,
                carryingAmountBase: line.CarryingAmountBase,
                documentCurrencyCode: document.TransactionCurrencyCode.Value,
                documentBaseCurrencyCode: document.BaseCurrencyCode.Value,
                settlementFxRate: document.TransactionCurrencyCode == document.BaseCurrencyCode
                    ? null
                    : document.FxSnapshot?.Rate,
                realizedFxAmount: document.TransactionCurrencyCode == document.BaseCurrencyCode
                    ? null
                    : SettlementAmountMath.RoundBase(line.CarryingAmountBase - line.AppliedAmountBase),
                partyId: document.PartyId,
                createdByUserId: createdByUserId,
                cancellationToken: cancellationToken);
        }
    }

    public async Task ApplyCreditApplicationAsync(
        CreditApplicationDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (await HasExistingApplicationsAsync(document.CompanyId, document.SourceType, document.Id, cancellationToken))
        {
            return;
        }

        foreach (var line in document.ApplicationLines)
        {
            await ApplySingleAsync(
                companyId: document.CompanyId,
                sourceType: document.SourceType,
                sourceId: document.Id,
                applicationType: "credit_application",
                targetOpenItemType: "ar_open_item",
                targetOpenItemId: line.SourceCreditArOpenItemId,
                appliedAmountTx: line.AppliedAmount,
                appliedAmountBase: line.SourceCarryingAmountBase,
                carryingAmountBase: line.SourceCarryingAmountBase,
                documentCurrencyCode: document.TransactionCurrencyCode.Value,
                documentBaseCurrencyCode: document.BaseCurrencyCode.Value,
                settlementFxRate: null,
                realizedFxAmount: null,
                partyId: document.PartyId,
                createdByUserId: createdByUserId,
                cancellationToken: cancellationToken);

            await ApplySingleAsync(
                companyId: document.CompanyId,
                sourceType: document.SourceType,
                sourceId: document.Id,
                applicationType: "credit_application",
                targetOpenItemType: "ar_open_item",
                targetOpenItemId: line.TargetInvoiceArOpenItemId,
                appliedAmountTx: line.AppliedAmount,
                appliedAmountBase: line.TargetCarryingAmountBase,
                carryingAmountBase: line.TargetCarryingAmountBase,
                documentCurrencyCode: document.TransactionCurrencyCode.Value,
                documentBaseCurrencyCode: document.BaseCurrencyCode.Value,
                settlementFxRate: null,
                realizedFxAmount: null,
                partyId: document.PartyId,
                createdByUserId: createdByUserId,
                cancellationToken: cancellationToken);
        }
    }

    public async Task ApplyVendorCreditApplicationAsync(
        VendorCreditApplicationDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (await HasExistingApplicationsAsync(document.CompanyId, document.SourceType, document.Id, cancellationToken))
        {
            return;
        }

        foreach (var line in document.ApplicationLines)
        {
            await ApplySingleAsync(
                companyId: document.CompanyId,
                sourceType: document.SourceType,
                sourceId: document.Id,
                applicationType: "vendor_credit_application",
                targetOpenItemType: "ap_open_item",
                targetOpenItemId: line.SourceVendorCreditApOpenItemId,
                appliedAmountTx: line.AppliedAmount,
                appliedAmountBase: line.SourceCarryingAmountBase,
                carryingAmountBase: line.SourceCarryingAmountBase,
                documentCurrencyCode: document.TransactionCurrencyCode.Value,
                documentBaseCurrencyCode: document.BaseCurrencyCode.Value,
                settlementFxRate: null,
                realizedFxAmount: null,
                partyId: document.PartyId,
                createdByUserId: createdByUserId,
                cancellationToken: cancellationToken);

            await ApplySingleAsync(
                companyId: document.CompanyId,
                sourceType: document.SourceType,
                sourceId: document.Id,
                applicationType: "vendor_credit_application",
                targetOpenItemType: "ap_open_item",
                targetOpenItemId: line.TargetBillApOpenItemId,
                appliedAmountTx: line.AppliedAmount,
                appliedAmountBase: line.TargetCarryingAmountBase,
                carryingAmountBase: line.TargetCarryingAmountBase,
                documentCurrencyCode: document.TransactionCurrencyCode.Value,
                documentBaseCurrencyCode: document.BaseCurrencyCode.Value,
                settlementFxRate: null,
                realizedFxAmount: null,
                partyId: document.PartyId,
                createdByUserId: createdByUserId,
                cancellationToken: cancellationToken);
        }
    }

    public async Task<IReadOnlyList<OpenItemApplicationDrillDown>> ListApplicationsAsync(
        CompanyId companyId,
        string targetOpenItemType,
        Guid targetOpenItemId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetOpenItemType);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var items = new List<OpenItemApplicationDrillDown>();

        await using var command = scope.CreateCommand(
            """
            select
              sa.id as application_id,
              sa.application_type,
              sa.source_type,
              sa.source_id as source_document_id,
              coalesce(
                rp.payment_number,
                ca.application_number,
                pb.payment_number,
                vca.application_number,
                sa.source_id::text) as source_document_display_number,
              coalesce(
                rp.payment_date,
                ca.application_date,
                pb.payment_date,
                vca.application_date) as source_document_date,
              sa.applied_amount_tx,
              sa.applied_amount_base,
              sa.settlement_fx_rate,
              sa.realized_fx_amount,
              sa.created_at
            from settlement_applications sa
            left join receive_payments rp
              on sa.source_type = 'receive_payment'
             and rp.company_id = sa.company_id
             and rp.id = sa.source_id
            left join credit_applications ca
              on sa.source_type = 'credit_application'
             and ca.company_id = sa.company_id
             and ca.id = sa.source_id
            left join pay_bills pb
              on sa.source_type = 'pay_bill'
             and pb.company_id = sa.company_id
             and pb.id = sa.source_id
            left join vendor_credit_applications vca
              on sa.source_type = 'vendor_credit_application'
             and vca.company_id = sa.company_id
             and vca.id = sa.source_id
            where sa.company_id = @company_id
              and sa.target_open_item_type = @target_open_item_type
              and sa.target_open_item_id = @target_open_item_id
            order by sa.created_at asc, sa.id asc;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("target_open_item_type", targetOpenItemType);
        command.Parameters.AddWithValue("target_open_item_id", targetOpenItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new OpenItemApplicationDrillDown(
                reader.GetGuid(reader.GetOrdinal("application_id")),
                reader.GetString(reader.GetOrdinal("application_type")),
                reader.GetString(reader.GetOrdinal("source_type")),
                reader.GetGuid(reader.GetOrdinal("source_document_id")),
                reader.GetString(reader.GetOrdinal("source_document_display_number")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("source_document_date")),
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                reader.GetDecimal(reader.GetOrdinal("applied_amount_base")),
                reader.IsDBNull(reader.GetOrdinal("settlement_fx_rate")) ? null : reader.GetDecimal(reader.GetOrdinal("settlement_fx_rate")),
                reader.IsDBNull(reader.GetOrdinal("realized_fx_amount")) ? null : reader.GetDecimal(reader.GetOrdinal("realized_fx_amount")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"))));
        }

        return items;
    }

    private async Task<bool> HasExistingApplicationsAsync(
        CompanyId companyId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            """
            select id
            from settlement_applications
            where company_id = @company_id
              and source_type = @source_type
              and source_id = @source_id
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);

        var existing = await command.ExecuteScalarAsync(cancellationToken);
        return existing is not null && existing != DBNull.Value;
    }

    private async Task ApplySingleAsync(
        CompanyId companyId,
        string sourceType,
        Guid sourceId,
        string applicationType,
        string targetOpenItemType,
        Guid targetOpenItemId,
        decimal appliedAmountTx,
        decimal appliedAmountBase,
        decimal carryingAmountBase,
        string documentCurrencyCode,
        string documentBaseCurrencyCode,
        decimal? settlementFxRate,
        decimal? realizedFxAmount,
        Guid partyId,
        UserId createdByUserId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var targetTable = targetOpenItemType == "ar_open_item" ? "ar_open_items" : "ap_open_items";
        var partyColumn = targetOpenItemType == "ar_open_item" ? "customer_id" : "vendor_id";

        decimal openAmountTx;
        decimal openAmountBase;
        string status;
        string baseCurrencyCode;

        await using (var selectCommand = scope.CreateCommand(
                         $"""
                         select
                           {partyColumn},
                           document_currency_code,
                           base_currency_code,
                           open_amount_tx,
                           open_amount_base,
                           status
                         from {targetTable}
                         where company_id = @company_id
                           and id = @target_open_item_id
                         for update;
                         """))
        {
            selectCommand.Parameters.AddWithValue("company_id", companyId.Value);
            selectCommand.Parameters.AddWithValue("target_open_item_id", targetOpenItemId);

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Settlement target open item was not found.");
            }

            var resolvedPartyId = reader.GetGuid(reader.GetOrdinal(partyColumn));
            if (resolvedPartyId != partyId)
            {
                throw new InvalidOperationException("Settlement target party does not match the payment document.");
            }

            var targetCurrency = reader.GetString(reader.GetOrdinal("document_currency_code"));
            if (!string.Equals(targetCurrency, documentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Settlement target currency does not match the payment document.");
            }

            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            openAmountTx = reader.GetDecimal(reader.GetOrdinal("open_amount_tx"));
            openAmountBase = reader.GetDecimal(reader.GetOrdinal("open_amount_base"));
            status = reader.GetString(reader.GetOrdinal("status"));
        }

        if (status is not ("open" or "partially_applied"))
        {
            throw new InvalidOperationException("Settlement target is not open for application.");
        }

        if (appliedAmountTx > openAmountTx)
        {
            throw new InvalidOperationException("Settlement application exceeds the open amount.");
        }

        if (!string.Equals(baseCurrencyCode, documentBaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Settlement target base currency does not match the payment document.");
        }

        if (carryingAmountBase > openAmountBase)
        {
            throw new InvalidOperationException("Settlement application exceeds the remaining carrying amount.");
        }

        var newOpenAmountTx = Math.Round(openAmountTx - appliedAmountTx, 6, MidpointRounding.ToEven);
        var newOpenAmountBase = Math.Round(openAmountBase - carryingAmountBase, 6, MidpointRounding.ToEven);
        var newStatus = newOpenAmountTx == 0m ? "closed" : "partially_applied";

        await using (var insertCommand = scope.CreateCommand(
                         """
                         insert into settlement_applications (
                           id,
                           company_id,
                           application_type,
                           source_type,
                           source_id,
                           target_open_item_type,
                           target_open_item_id,
                           applied_amount_tx,
                           applied_amount_base,
                           settlement_fx_rate,
                           realized_fx_amount,
                           created_at,
                           created_by_user_id
                         )
                         values (
                           @id,
                           @company_id,
                           @application_type,
                           @source_type,
                           @source_id,
                           @target_open_item_type,
                           @target_open_item_id,
                           @applied_amount_tx,
                           @applied_amount_base,
                           @settlement_fx_rate,
                           @realized_fx_amount,
                           now(),
                           @created_by_user_id
                         );
                         """))
        {
            insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertCommand.Parameters.AddWithValue("application_type", applicationType);
            insertCommand.Parameters.AddWithValue("source_type", sourceType);
            insertCommand.Parameters.AddWithValue("source_id", sourceId);
            insertCommand.Parameters.AddWithValue("target_open_item_type", targetOpenItemType);
            insertCommand.Parameters.AddWithValue("target_open_item_id", targetOpenItemId);
            insertCommand.Parameters.AddWithValue("applied_amount_tx", appliedAmountTx);
            insertCommand.Parameters.AddWithValue("applied_amount_base", appliedAmountBase);
            insertCommand.Parameters.AddWithValue(
                "settlement_fx_rate",
                settlementFxRate.HasValue ? (object)settlementFxRate.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "realized_fx_amount",
                realizedFxAmount.HasValue ? (object)realizedFxAmount.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue("created_by_user_id", createdByUserId.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var updateCommand = scope.CreateCommand(
            $"""
            update {targetTable}
            set open_amount_tx = @open_amount_tx,
                open_amount_base = @open_amount_base,
                status = @status,
                updated_at = now()
            where company_id = @company_id
              and id = @target_open_item_id;
            """);

        updateCommand.Parameters.AddWithValue("open_amount_tx", newOpenAmountTx);
        updateCommand.Parameters.AddWithValue("open_amount_base", newOpenAmountBase);
        updateCommand.Parameters.AddWithValue("status", newStatus);
        updateCommand.Parameters.AddWithValue("company_id", companyId.Value);
        updateCommand.Parameters.AddWithValue("target_open_item_id", targetOpenItemId);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
