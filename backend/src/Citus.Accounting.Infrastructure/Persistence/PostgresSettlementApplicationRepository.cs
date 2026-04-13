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

        if (await HasExistingApplicationsAsync(document.CompanyId.Value, document.SourceType, document.Id, cancellationToken))
        {
            return;
        }

        foreach (var line in document.PaymentLines)
        {
            await ApplySingleAsync(
                companyId: document.CompanyId.Value,
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
                createdByUserId: createdByUserId.Value,
                cancellationToken: cancellationToken);
        }
    }

    public async Task ApplyPayBillAsync(
        PayBillDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (await HasExistingApplicationsAsync(document.CompanyId.Value, document.SourceType, document.Id, cancellationToken))
        {
            return;
        }

        foreach (var line in document.PaymentLines)
        {
            await ApplySingleAsync(
                companyId: document.CompanyId.Value,
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
                createdByUserId: createdByUserId.Value,
                cancellationToken: cancellationToken);
        }
    }

    public async Task ApplyCreditApplicationAsync(
        CreditApplicationDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (await HasExistingApplicationsAsync(document.CompanyId.Value, document.SourceType, document.Id, cancellationToken))
        {
            return;
        }

        foreach (var line in document.ApplicationLines)
        {
            await ApplySingleAsync(
                companyId: document.CompanyId.Value,
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
                createdByUserId: createdByUserId.Value,
                cancellationToken: cancellationToken);

            await ApplySingleAsync(
                companyId: document.CompanyId.Value,
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
                createdByUserId: createdByUserId.Value,
                cancellationToken: cancellationToken);
        }
    }

    public async Task ApplyVendorCreditApplicationAsync(
        VendorCreditApplicationDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (await HasExistingApplicationsAsync(document.CompanyId.Value, document.SourceType, document.Id, cancellationToken))
        {
            return;
        }

        foreach (var line in document.ApplicationLines)
        {
            await ApplySingleAsync(
                companyId: document.CompanyId.Value,
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
                createdByUserId: createdByUserId.Value,
                cancellationToken: cancellationToken);

            await ApplySingleAsync(
                companyId: document.CompanyId.Value,
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
                createdByUserId: createdByUserId.Value,
                cancellationToken: cancellationToken);
        }
    }

    private async Task<bool> HasExistingApplicationsAsync(
        Guid companyId,
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

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);

        var existing = await command.ExecuteScalarAsync(cancellationToken);
        return existing is not null && existing != DBNull.Value;
    }

    private async Task ApplySingleAsync(
        Guid companyId,
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
        Guid createdByUserId,
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
            selectCommand.Parameters.AddWithValue("company_id", companyId);
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
            insertCommand.Parameters.AddWithValue("company_id", companyId);
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
            insertCommand.Parameters.AddWithValue("created_by_user_id", createdByUserId);
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
        updateCommand.Parameters.AddWithValue("company_id", companyId);
        updateCommand.Parameters.AddWithValue("target_open_item_id", targetOpenItemId);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
