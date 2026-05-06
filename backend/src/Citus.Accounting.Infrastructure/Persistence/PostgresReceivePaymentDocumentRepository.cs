using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Infrastructure;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresReceivePaymentDocumentRepository : IReceivePaymentDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresReceivePaymentDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<IReadOnlyList<SettlementOpenItemCandidate>> ListOpenReceivableCandidatesAsync(
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        return await LoadOpenReceivableCandidatesAsync(
            scope,
            companyId,
            customerId,
            null,
            forUpdate: false,
            cancellationToken);
    }

    public async Task<SettlementDraftPreparationResult> PrepareDraftAsync(
        ReceivePaymentDraftPreparation request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Lines.Count == 0)
        {
            throw new InvalidOperationException("Receive payment draft requires at least one application line.");
        }

        var duplicateTarget = request.Lines
            .GroupBy(static line => line.TargetOpenItemId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateTarget is not null)
        {
            throw new InvalidOperationException("Receive payment draft cannot target the same open item more than once.");
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureActiveCustomerAsync(scope, request.CompanyId, request.CustomerId, cancellationToken);
        await PostgresSettlementDraftingSupport.EnsureActiveBankAccountAsync(
            scope,
            request.CompanyId,
            request.BankAccountId,
            "Receive payment draft references a bank account outside the active company context or an inactive bank account.",
            cancellationToken);

        var requestedTargetIds = request.Lines
            .Select(static line => line.TargetOpenItemId)
            .ToArray();
        var candidates = await LoadOpenReceivableCandidatesAsync(
            scope,
            request.CompanyId,
            request.CustomerId,
            requestedTargetIds,
            forUpdate: true,
            cancellationToken);
        if (candidates.Count != requestedTargetIds.Length)
        {
            throw new InvalidOperationException("Receive payment draft contains an invalid or non-open AR target.");
        }

        var candidateMap = candidates.ToDictionary(static candidate => candidate.OpenItemId);
        var firstCandidate = candidates[0];
        var documentCurrencyCode = firstCandidate.DocumentCurrencyCode;
        if (candidates.Any(candidate => !string.Equals(candidate.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Receive payment draft lines must use the same transaction currency.");
        }

        var totalAmount = 0m;
        foreach (var line in request.Lines)
        {
            if (!candidateMap.TryGetValue(line.TargetOpenItemId, out var candidate))
            {
                throw new InvalidOperationException("Receive payment draft references an unknown AR open item.");
            }

            if (line.AppliedAmountTx <= 0m)
            {
                throw new InvalidOperationException("Receive payment draft line amounts must be positive.");
            }

            if (line.AppliedAmountTx > candidate.OpenAmountTx)
            {
                throw new InvalidOperationException("Receive payment draft line exceeds the open AR amount.");
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
                  "Receive payment draft could not find an acceptable FX snapshot for the payment date.");

        var draftId = Guid.NewGuid();
        var entityNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            request.CompanyId,
            $"entity-number:receive-payment:{request.PaymentDate:yyyy}",
            $"EN{request.PaymentDate:yyyy}",
            padding: EntityNumber.OrdinalWidth,
            cancellationToken);
        var paymentNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            request.CompanyId,
            "receive-payment-display",
            "RCP-",
            padding: 6,
            cancellationToken);

        await InsertDraftHeaderAsync(
            scope,
            draftId,
            request.CompanyId,
            request.UserId,
            entityNumber,
            paymentNumber,
            request.CustomerId,
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

    public async Task<ReceivePaymentDocument?> GetForPostingAsync(
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
        Guid customerId;
        Guid bankAccountId;
        Guid receivableAccountId;
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
                           rp.id,
                           rp.entity_number,
                           rp.payment_number,
                           rp.status,
                           rp.payment_date,
                           rp.customer_id,
                           rp.bank_account_id,
                           rp.document_currency_code,
                           rp.base_currency_code,
                           rp.fx_rate_snapshot_id,
                           rp.fx_rate,
                           rp.fx_requested_date,
                           rp.fx_effective_date,
                           rp.fx_source,
                           rp.total_amount,
                           rp.memo,
                           (
                             select a.id
                             from accounts a
                             where a.company_id = rp.company_id
                               and a.is_active = true
                               and (
                                 (rp.document_currency_code = rp.base_currency_code and (a.system_role = 'accounts_receivable' or a.code = '1100'))
                                 or
                                 (rp.document_currency_code <> rp.base_currency_code and (a.system_role = ('accounts_receivable:' || rp.document_currency_code) or a.code = ('AR-' || rp.document_currency_code)))
                               )
                             order by
                               case
                                 when rp.document_currency_code = rp.base_currency_code and a.system_role = 'accounts_receivable' then 0
                                 when rp.document_currency_code <> rp.base_currency_code and a.system_role = ('accounts_receivable:' || rp.document_currency_code) then 0
                                 else 1
                               end,
                               a.code
                             limit 1
                           ) as receivable_account_id
                         from receive_payments rp
                         where rp.company_id = @company_id
                           and rp.id = @document_id
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
            customerId = reader.GetGuid(reader.GetOrdinal("customer_id"));
            bankAccountId = reader.GetGuid(reader.GetOrdinal("bank_account_id"));
            receivableAccountId = reader.IsDBNull(reader.GetOrdinal("receivable_account_id"))
                ? Guid.Empty
                : reader.GetGuid(reader.GetOrdinal("receivable_account_id"));
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

        if (receivableAccountId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Receive payment routing could not resolve an active Accounts Receivable control account.");
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
                "Receive payment document references a bank account outside the active company context or an inactive bank account.");
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
                    "Receive payment routing could not resolve active realized FX gain/loss accounts. Configure accounts.system_role or accounts.system_key with 'realized_fx_gain' and 'realized_fx_loss'.");
            }
        }

        var rawLines = new List<(int LineNumber, Guid TargetOpenItemId, decimal AppliedAmountTx)>();
        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.target_ar_open_item_id,
                           l.applied_amount_tx
                         from receive_payment_lines l
                         where l.company_id = @company_id
                           and l.receive_payment_id = @document_id
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
                    reader.GetGuid(reader.GetOrdinal("target_ar_open_item_id")),
                    reader.GetDecimal(reader.GetOrdinal("applied_amount_tx"))));
            }
        }

        var appliedTotal = rawLines.Sum(static line => line.AppliedAmountTx);
        if (appliedTotal != totalAmount)
        {
            throw new InvalidOperationException("Receive payment total must equal the sum of its application lines.");
        }

        var appliedAmountBases = SettlementAmountMath.AllocateSettlementBaseAmounts(
            rawLines.Select(static line => line.AppliedAmountTx).ToArray(),
            totalAmount,
            fxRate);

        var lines = new List<ReceivePaymentDocumentLine>();
        for (var index = 0; index < rawLines.Count; index++)
        {
            var rawLine = rawLines[index];
            var target = await LoadArOpenItemAsync(
                scope,
                companyId,
                rawLine.TargetOpenItemId,
                cancellationToken);

            if (target.CustomerId != customerId)
            {
                throw new InvalidOperationException("Receive payment lines must target open items from the same customer.");
            }

            if (!string.Equals(target.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Receive payment lines must use the same transaction currency as the payment document.");
            }

            if (target.Status is not ("open" or "partially_applied"))
            {
                throw new InvalidOperationException("Receive payment lines may only target open AR items.");
            }

            if (rawLine.AppliedAmountTx > target.OpenAmountTx)
            {
                throw new InvalidOperationException("Receive payment line exceeds the current open amount.");
            }

            var carryingAmountBase = SettlementAmountMath.CalculateCarryingAmountBase(
                rawLine.AppliedAmountTx,
                target.OpenAmountTx,
                target.OpenAmountBase);

            lines.Add(new ReceivePaymentDocumentLine(
                rawLine.LineNumber,
                rawLine.TargetOpenItemId,
                $"Receive payment application line {rawLine.LineNumber}",
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

        return new ReceivePaymentDocument(
            id,
            companyId,
            EntityNumber.Parse(entityNumber),
            new DocumentNumber(paymentNumber),
            status,
            paymentDate,
            customerId,
            bankAccountId,
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

    private static async Task<IReadOnlyList<SettlementOpenItemCandidate>> LoadOpenReceivableCandidatesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid customerId,
        Guid[]? openItemIds,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            select
              oi.id,
              oi.source_type,
              oi.source_id,
              coalesce(i.invoice_number, cn.credit_note_number, oi.source_id::text) as display_number,
              coalesce(i.invoice_date, cn.credit_note_date, oi.due_date) as document_date,
              oi.due_date,
              oi.document_currency_code,
              oi.base_currency_code,
              oi.original_amount_tx,
              oi.open_amount_tx,
              oi.open_amount_base,
              oi.balance_side,
              oi.status
            from ar_open_items oi
            left join invoices i
              on oi.source_type = 'invoice'
             and i.company_id = oi.company_id
             and i.id = oi.source_id
            left join credit_notes cn
              on oi.source_type = 'credit_note'
             and cn.company_id = oi.company_id
             and cn.id = oi.source_id
            where oi.company_id = @company_id
              and oi.customer_id = @customer_id
              and oi.status in ('open', 'partially_applied')
              and oi.balance_side = 'debit'
              and oi.open_amount_tx > 0
              {(openItemIds is null ? string.Empty : "and oi.id = any(@open_item_ids)")}
            order by oi.due_date asc nulls first, document_date asc, oi.created_at asc, oi.id asc
            {(forUpdate ? "for update of oi" : string.Empty)};
            """;

        await using var command = scope.CreateCommand(sql);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);

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

    private static async Task EnsureActiveCustomerAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id
            from customers
            where company_id = @company_id
              and id = @customer_id
              and is_active = true
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            throw new InvalidOperationException("Receive payment draft references a customer outside the active company context or an inactive customer.");
        }
    }

    private static async Task InsertDraftHeaderAsync(
        PostgresCommandScope scope,
        Guid documentId,
        CompanyId companyId,
        UserId userId,
        string entityNumber,
        string paymentNumber,
        Guid customerId,
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
            insert into receive_payments (
              id,
              company_id,
              entity_number,
              payment_number,
              customer_id,
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
              @customer_id,
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("payment_number", paymentNumber);
        command.Parameters.AddWithValue("customer_id", customerId);
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
        command.Parameters.AddWithValue("created_by_user_id", userId.Value);

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
                insert into receive_payment_lines (
                  company_id,
                  receive_payment_id,
                  line_number,
                  target_ar_open_item_id,
                  applied_amount_tx
                )
                values (
                  @company_id,
                  @receive_payment_id,
                  @line_number,
                  @target_ar_open_item_id,
                  @applied_amount_tx
                );
                """);

            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("receive_payment_id", documentId);
            command.Parameters.AddWithValue("line_number", index + 1);
            command.Parameters.AddWithValue("target_ar_open_item_id", line.TargetOpenItemId);
            command.Parameters.AddWithValue("applied_amount_tx", line.AppliedAmountTx);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
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
              document_currency_code,
              status,
              open_amount_tx,
              open_amount_base
            from ar_open_items
            where company_id = @company_id
              and id = @open_item_id
            for update;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("open_item_id", openItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Receive payment line references an AR open item that does not exist.");
        }

        return new ArOpenItemTarget(
            reader.GetGuid(reader.GetOrdinal("customer_id")),
            reader.GetString(reader.GetOrdinal("document_currency_code")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_base")));
    }

    private sealed record ArOpenItemTarget(
        Guid CustomerId,
        string DocumentCurrencyCode,
        string Status,
        decimal OpenAmountTx,
        decimal OpenAmountBase);
}
