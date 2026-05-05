using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Infrastructure;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresPayBillDocumentRepository : IPayBillDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresPayBillDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<IReadOnlyList<SettlementOpenItemCandidate>> ListOpenPayableCandidatesAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        return await LoadOpenPayableCandidatesAsync(
            scope,
            companyId,
            vendorId,
            null,
            forUpdate: false,
            cancellationToken);
    }

    public async Task<SettlementDraftPreparationResult> PrepareDraftAsync(
        PayBillDraftPreparation request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Lines.Count == 0)
        {
            throw new InvalidOperationException("Pay bill draft requires at least one application line.");
        }

        var duplicateTarget = request.Lines
            .GroupBy(static line => line.TargetOpenItemId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateTarget is not null)
        {
            throw new InvalidOperationException("Pay bill draft cannot target the same open item more than once.");
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureActiveVendorAsync(scope, request.CompanyId, request.VendorId, cancellationToken);
        await PostgresSettlementDraftingSupport.EnsureActiveBankAccountAsync(
            scope,
            request.CompanyId,
            request.BankAccountId,
            "Pay bill draft references a bank account outside the active company context or an inactive bank account.",
            cancellationToken);

        var requestedTargetIds = request.Lines
            .Select(static line => line.TargetOpenItemId)
            .ToArray();
        var candidates = await LoadOpenPayableCandidatesAsync(
            scope,
            request.CompanyId,
            request.VendorId,
            requestedTargetIds,
            forUpdate: true,
            cancellationToken);
        if (candidates.Count != requestedTargetIds.Length)
        {
            throw new InvalidOperationException("Pay bill draft contains an invalid or non-open AP target.");
        }

        var candidateMap = candidates.ToDictionary(static candidate => candidate.OpenItemId);
        var firstCandidate = candidates[0];
        var documentCurrencyCode = firstCandidate.DocumentCurrencyCode;
        if (candidates.Any(candidate => !string.Equals(candidate.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Pay bill draft lines must use the same transaction currency.");
        }

        var totalAmount = 0m;
        foreach (var line in request.Lines)
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

            totalAmount += line.AppliedAmountTx;
        }

        var baseCurrencyCode = await PostgresSettlementDraftingSupport.LoadCompanyBaseCurrencyCodeAsync(
            scope,
            request.CompanyId,
            cancellationToken);
        var fxSnapshot = string.Equals(documentCurrencyCode, baseCurrencyCode, StringComparison.OrdinalIgnoreCase)
            ? PostgresSettlementDraftingSupport.CreateIdentitySnapshot(baseCurrencyCode, request.PaymentDate)
            : await PostgresSettlementDraftingSupport.LoadAcceptedFxSnapshotAsync(
                scope,
                request.CompanyId,
                baseCurrencyCode,
                documentCurrencyCode,
                request.PaymentDate,
                request.AcceptedFxSnapshotId,
                cancellationToken)
              ?? throw new InvalidOperationException(
                  "Pay bill draft could not find an acceptable FX snapshot for the payment date.");

        var draftId = Guid.NewGuid();
        var entityNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            request.CompanyId,
            $"entity-number:pay-bill:{request.PaymentDate:yyyy}",
            $"EN{request.PaymentDate:yyyy}",
            padding: 8,
            cancellationToken);
        var paymentNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            request.CompanyId,
            "pay-bill-display",
            "PB-",
            padding: 6,
            cancellationToken);

        await InsertDraftHeaderAsync(
            scope,
            draftId,
            request.CompanyId,
            request.UserId,
            entityNumber,
            paymentNumber,
            request.VendorId,
            request.BankAccountId,
            request.PaymentDate,
            documentCurrencyCode,
            baseCurrencyCode,
            fxSnapshot,
            totalAmount,
            request.Memo,
            cancellationToken);
        await InsertDraftLinesAsync(
            scope,
            request.CompanyId,
            draftId,
            request.Lines,
            cancellationToken);

        return new SettlementDraftPreparationResult(
            draftId,
            entityNumber,
            paymentNumber,
            request.Lines.Count,
            totalAmount,
            "draft");
    }

    public async Task<PayBillDocument?> GetForPostingAsync(
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
        string paymentNumber;
        string status;
        DateOnly paymentDate;
        Guid vendorId;
        Guid bankAccountId;
        Guid payableAccountId;
        Guid? realizedFxGainAccountId = null;
        Guid? realizedFxLossAccountId = null;
        string documentCurrencyCode;
        string baseCurrencyCode;
        Guid? fxSnapshotId;
        decimal fxRate;
        DateOnly fxRequestedDate;
        DateOnly fxEffectiveDate;
        string fxSource;
        decimal totalAmount;
        string? memo;

        await using (var headerCommand = scope.CreateCommand(
                         """
                         select
                           pb.id,
                           pb.entity_number,
                           pb.payment_number,
                           pb.status,
                           pb.payment_date,
                           pb.vendor_id,
                           pb.bank_account_id,
                           pb.document_currency_code,
                           pb.base_currency_code,
                           pb.fx_rate_snapshot_id,
                           pb.fx_rate,
                           pb.fx_requested_date,
                           pb.fx_effective_date,
                           pb.fx_source,
                           pb.total_amount,
                           pb.memo,
                           (
                             select a.id
                             from accounts a
                             where a.company_id = pb.company_id
                               and a.is_active = true
                               and (
                                 (pb.document_currency_code = pb.base_currency_code and (a.system_role = 'accounts_payable' or a.code = '2000'))
                                 or
                                 (pb.document_currency_code <> pb.base_currency_code and (a.system_role = ('accounts_payable:' || pb.document_currency_code) or a.code = ('AP-' || pb.document_currency_code)))
                               )
                             order by
                               case
                                 when pb.document_currency_code = pb.base_currency_code and a.system_role = 'accounts_payable' then 0
                                 when pb.document_currency_code <> pb.base_currency_code and a.system_role = ('accounts_payable:' || pb.document_currency_code) then 0
                                 else 1
                               end,
                               a.code
                             limit 1
                           ) as payable_account_id
                         from pay_bills pb
                         where pb.company_id = @company_id
                           and pb.id = @document_id
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
            paymentNumber = reader.GetString(reader.GetOrdinal("payment_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            paymentDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("payment_date"));
            vendorId = reader.GetGuid(reader.GetOrdinal("vendor_id"));
            bankAccountId = reader.GetGuid(reader.GetOrdinal("bank_account_id"));
            payableAccountId = reader.IsDBNull(reader.GetOrdinal("payable_account_id"))
                ? Guid.Empty
                : reader.GetGuid(reader.GetOrdinal("payable_account_id"));
            documentCurrencyCode = reader.GetString(reader.GetOrdinal("document_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            fxSnapshotId = reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id"));
            fxRate = reader.GetDecimal(reader.GetOrdinal("fx_rate"));
            fxRequestedDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_requested_date"));
            fxEffectiveDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_effective_date"));
            fxSource = reader.GetString(reader.GetOrdinal("fx_source"));
            totalAmount = reader.GetDecimal(reader.GetOrdinal("total_amount"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo"));
        }

        if (payableAccountId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Pay bill routing could not resolve an active Accounts Payable control account.");
        }

        await using (var bankAccountCommand = scope.CreateCommand(
                         """
                         select id
                         from accounts
                         where company_id = @company_id
                           and id = @bank_account_id
                           and is_active = true
                         limit 1;
                         """))
        {
            bankAccountCommand.Parameters.AddWithValue("company_id", companyId.Value);
            bankAccountCommand.Parameters.AddWithValue("bank_account_id", bankAccountId);
            var bankAccount = await bankAccountCommand.ExecuteScalarAsync(cancellationToken);
            if (bankAccount is null || bankAccount == DBNull.Value)
        {
            throw new InvalidOperationException(
                "Pay bill document references a bank account outside the active company context or an inactive bank account.");
            }
        }

        if (!string.Equals(documentCurrencyCode, baseCurrencyCode, StringComparison.OrdinalIgnoreCase))
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
                    "Pay bill routing could not resolve active realized FX gain/loss accounts. Configure accounts.system_role or accounts.system_key with 'realized_fx_gain' and 'realized_fx_loss'.");
            }
        }

        var rawLines = new List<(int LineNumber, Guid TargetOpenItemId, decimal AppliedAmountTx)>();
        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.target_ap_open_item_id,
                           l.applied_amount_tx
                         from pay_bill_lines l
                         where l.company_id = @company_id
                           and l.pay_bill_id = @document_id
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
                    reader.GetGuid(reader.GetOrdinal("target_ap_open_item_id")),
                    reader.GetDecimal(reader.GetOrdinal("applied_amount_tx"))));
            }
        }

        var appliedTotal = rawLines.Sum(static line => line.AppliedAmountTx);
        if (appliedTotal != totalAmount)
        {
            throw new InvalidOperationException("Pay bill total must equal the sum of its application lines.");
        }

        var appliedAmountBases = SettlementAmountMath.AllocateSettlementBaseAmounts(
            rawLines.Select(static line => line.AppliedAmountTx).ToArray(),
            totalAmount,
            fxRate);

        var lines = new List<PayBillDocumentLine>();
        for (var index = 0; index < rawLines.Count; index++)
        {
            var rawLine = rawLines[index];
            var target = await LoadApOpenItemAsync(
                scope,
                companyId,
                rawLine.TargetOpenItemId,
                cancellationToken);

            if (target.VendorId != vendorId)
            {
                throw new InvalidOperationException("Pay bill lines must target open items from the same vendor.");
            }

            if (!string.Equals(target.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Pay bill lines must use the same transaction currency as the payment document.");
            }

            if (target.Status is not ("open" or "partially_applied"))
            {
                throw new InvalidOperationException("Pay bill lines may only target open AP items.");
            }

            if (rawLine.AppliedAmountTx > target.OpenAmountTx)
            {
                throw new InvalidOperationException("Pay bill line exceeds the current open amount.");
            }

            var carryingAmountBase = SettlementAmountMath.CalculateCarryingAmountBase(
                rawLine.AppliedAmountTx,
                target.OpenAmountTx,
                target.OpenAmountBase);

            lines.Add(new PayBillDocumentLine(
                rawLine.LineNumber,
                rawLine.TargetOpenItemId,
                $"Pay bill application line {rawLine.LineNumber}",
                rawLine.AppliedAmountTx,
                appliedAmountBases[index],
                carryingAmountBase));
        }

        var transactionCurrency = new CurrencyCode(documentCurrencyCode);
        var baseCurrency = new CurrencyCode(baseCurrencyCode);
        FxSnapshotRef? fxSnapshot = null;
        if (fxSnapshotId.HasValue || transactionCurrency != baseCurrency || fxRate != 1m)
        {
            fxSnapshot = new FxSnapshotRef(
                fxSnapshotId ?? Guid.Empty,
                baseCurrency,
                transactionCurrency,
                fxRate,
                fxRequestedDate,
                fxEffectiveDate,
                fxSource);
        }

        return new PayBillDocument(
            id,
            companyId,
            new EntityNumber(entityNumber),
            new DocumentNumber(paymentNumber),
            status,
            paymentDate,
            vendorId,
            bankAccountId,
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

    private static async Task<IReadOnlyList<SettlementOpenItemCandidate>> LoadOpenPayableCandidatesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid vendorId,
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
              {(openItemIds is null ? string.Empty : "and oi.id = any(@open_item_ids)")}
            order by oi.due_date asc nulls first, document_date asc, oi.created_at asc, oi.id asc
            {(forUpdate ? "for update of oi" : string.Empty)};
            """;

        await using var command = scope.CreateCommand(sql);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("vendor_id", vendorId);

        if (openItemIds is not null)
        {
            command.Parameters.AddWithValue("open_item_ids", openItemIds);
        }

        var candidates = new List<SettlementOpenItemCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new SettlementOpenItemCandidate(
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
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id
            from vendors
            where company_id = @company_id
              and id = @vendor_id
              and is_active = true
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("vendor_id", vendorId);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            throw new InvalidOperationException("Pay bill draft references a vendor outside the active company context or an inactive vendor.");
        }
    }

    private static async Task InsertDraftHeaderAsync(
        PostgresCommandScope scope,
        Guid documentId,
        CompanyId companyId,
        UserId userId,
        string entityNumber,
        string paymentNumber,
        Guid vendorId,
        Guid bankAccountId,
        DateOnly paymentDate,
        string documentCurrencyCode,
        string baseCurrencyCode,
        FxSnapshotRef fxSnapshot,
        decimal totalAmount,
        string? memo,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
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
            """);

        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("payment_number", paymentNumber);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("payment_date", paymentDate);
        command.Parameters.AddWithValue("bank_account_id", bankAccountId);
        command.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("fx_rate_snapshot_id", fxSnapshot.SnapshotId == Guid.Empty ? DBNull.Value : fxSnapshot.SnapshotId);
        command.Parameters.AddWithValue("fx_rate", fxSnapshot.Rate);
        command.Parameters.AddWithValue("fx_requested_date", fxSnapshot.RequestedDate);
        command.Parameters.AddWithValue("fx_effective_date", fxSnapshot.EffectiveDate);
        command.Parameters.AddWithValue("fx_source", fxSnapshot.SourceSemantics);
        command.Parameters.AddWithValue("total_amount", totalAmount);
        command.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(memo) ? DBNull.Value : memo.Trim());
        command.Parameters.AddWithValue("created_by_user_id", userId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDraftLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        IReadOnlyList<SettlementDraftLine> lines,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            await using var command = scope.CreateCommand(
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
                """);

            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("pay_bill_id", documentId);
            command.Parameters.AddWithValue("line_number", index + 1);
            command.Parameters.AddWithValue("target_ap_open_item_id", line.TargetOpenItemId);
            command.Parameters.AddWithValue("applied_amount_tx", line.AppliedAmountTx);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<ApOpenItemTarget> LoadApOpenItemAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              vendor_id,
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
            throw new InvalidOperationException("Pay bill line references an AP open item that does not exist.");
        }

        return new ApOpenItemTarget(
            reader.GetGuid(reader.GetOrdinal("vendor_id")),
            reader.GetString(reader.GetOrdinal("document_currency_code")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_base")));
    }

    private sealed record ApOpenItemTarget(
        Guid VendorId,
        string DocumentCurrencyCode,
        string Status,
        decimal OpenAmountTx,
        decimal OpenAmountBase);
}
