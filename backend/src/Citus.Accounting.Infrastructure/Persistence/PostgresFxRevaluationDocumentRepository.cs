using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Infrastructure;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresFxRevaluationDocumentRepository : IFxRevaluationDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresFxRevaluationDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public Task<FxRevaluationDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        return GetForPostingCoreAsync(companyId, documentId, cancellationToken);
    }

    public Task<FxRevaluationCascadeUnwindPlanResult> GetCascadeUnwindPlanAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        return GetCascadeUnwindPlanCoreAsync(companyId, documentId, cancellationToken);
    }

    public Task<IReadOnlyList<FxRevaluationBatchListItem>> ListRecentAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken)
    {
        return ListRecentCoreAsync(companyId, take, cancellationToken);
    }

    public Task<FxRevaluationDraftPreparationResult> PrepareDraftAsync(
        FxRevaluationDraftPreparation request,
        CancellationToken cancellationToken)
    {
        return PrepareDraftCoreAsync(request, cancellationToken);
    }

    public Task<FxRevaluationDraftPreparationResult> PrepareNextPeriodUnwindDraftAsync(
        FxRevaluationUnwindPreparation request,
        CancellationToken cancellationToken)
    {
        return PrepareNextPeriodUnwindDraftCoreAsync(request, cancellationToken);
    }

    private async Task<FxRevaluationDocument?> GetForPostingCoreAsync(
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
        string displayNumber;
        string status;
        string batchKind;
        Guid? reversalOfDocumentId;
        Guid? bookId;
        string? bookCode;
        string? accountingStandard;
        string? revaluationProfile;
        string? fxRoundingPolicy;
        DateOnly revaluationDate;
        string transactionCurrencyCode;
        string baseCurrencyCode;
        Guid? fxSnapshotId;
        decimal fxRate;
        string fxRateType;
        string fxQuoteBasis;
        string fxRateUseCase;
        string fxPostingReason;
        DateOnly fxRequestedDate;
        DateOnly fxEffectiveDate;
        string fxSource;
        string? memo;
        var hasBatchGovernanceColumns = await FxBatchGovernanceColumnsExistAsync(scope, cancellationToken);
        var hasBatchRateMetadataColumns = await FxBatchRateMetadataColumnsExistAsync(scope, cancellationToken);
        var governanceProjection = hasBatchGovernanceColumns
            ? """
                           b.company_book_id,
                           b.book_code,
                           b.accounting_standard,
                           b.revaluation_profile,
                           b.fx_rounding_policy,
              """
            : """
                           null::uuid as company_book_id,
                           'PRIMARY'::text as book_code,
                           'ASPE'::text as accounting_standard,
                           'monetary_open_item_closing'::text as revaluation_profile,
                           'currency_precision'::text as fx_rounding_policy,
              """;
        var rateMetadataProjection = hasBatchRateMetadataColumns
            ? """
                           b.rate_type,
                           b.quote_basis,
                           b.rate_use_case,
                           b.posting_reason,
              """
            : """
                           'closing'::text as rate_type,
                           'direct'::text as quote_basis,
                           'remeasurement'::text as rate_use_case,
                           'revaluation'::text as posting_reason,
              """;

        await using (var headerCommand = scope.CreateCommand(
                         $"""
                         select
                           b.id,
                           b.entity_number,
                           b.display_number,
                           b.status,
                           b.batch_kind,
                           b.reversal_of_fx_revaluation_batch_id,
                           {governanceProjection}
                           b.revaluation_date,
                           b.transaction_currency_code,
                           b.base_currency_code,
                           b.fx_rate_snapshot_id,
                           b.fx_rate,
                           {rateMetadataProjection}
                           b.fx_requested_date,
                           b.fx_effective_date,
                           b.fx_source,
                           b.memo
                         from fx_revaluation_batches b
                         where b.company_id = @company_id
                           and b.id = @document_id
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
            displayNumber = reader.GetString(reader.GetOrdinal("display_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            batchKind = reader.GetString(reader.GetOrdinal("batch_kind"));
            reversalOfDocumentId = reader.IsDBNull(reader.GetOrdinal("reversal_of_fx_revaluation_batch_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("reversal_of_fx_revaluation_batch_id"));
            bookId = reader.IsDBNull(reader.GetOrdinal("company_book_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("company_book_id"));
            bookCode = reader.IsDBNull(reader.GetOrdinal("book_code"))
                ? null
                : reader.GetString(reader.GetOrdinal("book_code"));
            accountingStandard = reader.IsDBNull(reader.GetOrdinal("accounting_standard"))
                ? null
                : reader.GetString(reader.GetOrdinal("accounting_standard"));
            revaluationProfile = reader.IsDBNull(reader.GetOrdinal("revaluation_profile"))
                ? null
                : reader.GetString(reader.GetOrdinal("revaluation_profile"));
            fxRoundingPolicy = reader.IsDBNull(reader.GetOrdinal("fx_rounding_policy"))
                ? null
                : reader.GetString(reader.GetOrdinal("fx_rounding_policy"));
            revaluationDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("revaluation_date"));
            transactionCurrencyCode = reader.GetString(reader.GetOrdinal("transaction_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            fxSnapshotId = reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id"));
            fxRate = reader.GetDecimal(reader.GetOrdinal("fx_rate"));
            fxRateType = reader.GetString(reader.GetOrdinal("rate_type"));
            fxQuoteBasis = reader.GetString(reader.GetOrdinal("quote_basis"));
            fxRateUseCase = reader.GetString(reader.GetOrdinal("rate_use_case"));
            fxPostingReason = reader.GetString(reader.GetOrdinal("posting_reason"));
            fxRequestedDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_requested_date"));
            fxEffectiveDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_effective_date"));
            fxSource = reader.GetString(reader.GetOrdinal("fx_source"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
        }

        var unrealizedFxGainAccountId = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
            scope,
            companyId,
            cancellationToken,
            "unrealized_fx_gain",
            "fx_gain_unrealized");
        var unrealizedFxLossAccountId = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
            scope,
            companyId,
            cancellationToken,
            "unrealized_fx_loss",
            "fx_loss_unrealized");

        if (!unrealizedFxGainAccountId.HasValue || !unrealizedFxLossAccountId.HasValue)
        {
            throw new InvalidOperationException(
                "FX revaluation routing could not resolve active unrealized FX gain/loss accounts. Configure accounts.system_role or accounts.system_key with 'unrealized_fx_gain' and 'unrealized_fx_loss'.");
        }

        var lineRows = new List<FxRevaluationLineRow>();
        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.target_open_item_type,
                           l.target_open_item_id,
                           l.target_balance_side,
                           l.target_control_account_id,
                           l.offset_account_id,
                           l.party_id,
                           l.description,
                           l.open_amount_tx,
                           l.carrying_amount_base,
                           l.revalued_amount_base,
                           l.unrealized_fx_amount,
                           l.applied_at
                         from fx_revaluation_batch_lines l
                         where l.company_id = @company_id
                           and l.fx_revaluation_batch_id = @document_id
                         order by l.line_number asc;
                         """))
        {
            linesCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linesCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lineRows.Add(new FxRevaluationLineRow(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetString(reader.GetOrdinal("target_open_item_type")),
                    reader.GetGuid(reader.GetOrdinal("target_open_item_id")),
                    reader.GetString(reader.GetOrdinal("target_balance_side")),
                    reader.GetGuid(reader.GetOrdinal("target_control_account_id")),
                    reader.GetGuid(reader.GetOrdinal("offset_account_id")),
                    reader.GetGuid(reader.GetOrdinal("party_id")),
                    reader.GetString(reader.GetOrdinal("description")),
                    reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
                    reader.GetDecimal(reader.GetOrdinal("carrying_amount_base")),
                    reader.GetDecimal(reader.GetOrdinal("revalued_amount_base")),
                    reader.GetDecimal(reader.GetOrdinal("unrealized_fx_amount")),
                    reader.IsDBNull(reader.GetOrdinal("applied_at"))
                        ? (DateTimeOffset?)null
                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("applied_at"))));
            }
        }

        if (lineRows.Count == 0)
        {
            throw new InvalidOperationException("FX revaluation batch does not contain any lines.");
        }

        if (status == "draft")
        {
            foreach (var lineRow in lineRows)
            {
                var currentOpenItem = await LoadCurrentOpenItemAsync(
                    scope,
                    companyId,
                    lineRow.TargetOpenItemType,
                    lineRow.TargetOpenItemId,
                    cancellationToken);

                if (currentOpenItem.PartyId != lineRow.PartyId)
                {
                    throw new InvalidOperationException(
                        $"FX revaluation batch line {lineRow.LineNumber} no longer matches the target party.");
                }

                if (!string.Equals(currentOpenItem.BalanceSide, lineRow.TargetBalanceSide, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"FX revaluation batch line {lineRow.LineNumber} is stale because the target open item balance side changed after preparation.");
                }

                if (currentOpenItem.Status is not ("open" or "partially_applied"))
                {
                    throw new InvalidOperationException(
                        $"FX revaluation batch line {lineRow.LineNumber} targets an open item that is no longer open.");
                }

                if (Round6(currentOpenItem.OpenAmountTx) != Round6(lineRow.OpenAmountTx) ||
                    Round6(currentOpenItem.OpenAmountBase) != Round6(lineRow.CarryingAmountBase))
                {
                    throw new InvalidOperationException(
                        $"FX revaluation batch line {lineRow.LineNumber} is stale because the target open item balance changed after preparation.");
                }
            }
        }

        var lines = lineRows
            .Select(line => new FxRevaluationDocumentLine(
                line.LineNumber,
                line.TargetOpenItemType,
                line.TargetOpenItemId,
                line.TargetBalanceSide,
                line.TargetControlAccountId,
                line.OffsetAccountId,
                line.PartyId,
                line.Description,
                line.OpenAmountTx,
                line.CarryingAmountBase,
                line.RevaluedAmountBase,
                line.UnrealizedFxAmount))
            .ToArray();

        return new FxRevaluationDocument(
            id,
            companyId,
            EntityNumber.Parse(entityNumber),
            new DocumentNumber(displayNumber),
            status,
            revaluationDate,
            new CurrencyCode(transactionCurrencyCode),
            new CurrencyCode(baseCurrencyCode),
            new FxSnapshotRef(
                SnapshotId: fxSnapshotId ?? Guid.Empty,
                BaseCurrencyCode: new CurrencyCode(baseCurrencyCode),
                QuoteCurrencyCode: new CurrencyCode(transactionCurrencyCode),
                Rate: fxRate,
                RequestedDate: fxRequestedDate,
                EffectiveDate: fxEffectiveDate,
                SourceSemantics: fxSource,
                RateType: fxRateType,
                QuoteBasis: fxQuoteBasis,
                RateUseCase: fxRateUseCase,
                PostingReason: fxPostingReason),
            unrealizedFxGainAccountId.Value,
            unrealizedFxLossAccountId.Value,
            lines,
            memo,
            batchKind,
            reversalOfDocumentId,
            bookId,
            bookCode,
            accountingStandard,
            revaluationProfile,
            fxRoundingPolicy);
    }

    private async Task<IReadOnlyList<FxRevaluationBatchListItem>> ListRecentCoreAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken)
    {
        var effectiveTake = Math.Clamp(take, 1, 250);
        var items = new List<FxRevaluationBatchListItem>();

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);
        var hasBatchGovernanceColumns = await FxBatchGovernanceColumnsExistAsync(scope, cancellationToken);

        await using var command = scope.CreateCommand(
            hasBatchGovernanceColumns
                ? """
            select
              b.id,
              b.entity_number,
              b.display_number,
              b.status,
              b.batch_kind,
              b.reversal_of_fx_revaluation_batch_id,
              b.company_book_id,
              b.book_code,
              b.accounting_standard,
              b.revaluation_profile,
              b.fx_rounding_policy,
              b.revaluation_date,
              b.transaction_currency_code,
              b.base_currency_code,
              b.fx_rate_snapshot_id,
              b.fx_rate,
              count(l.id)::int as line_count,
              coalesce(sum(l.unrealized_fx_amount), 0) as unrealized_total_base,
              linked_je.id as linked_journal_entry_id,
              linked_je.display_number as linked_journal_entry_display_number,
              linked_je.posted_at as linked_journal_posted_at,
              b.created_at,
              b.updated_at
            from fx_revaluation_batches b
            left join fx_revaluation_batch_lines l
              on l.company_id = b.company_id
             and l.fx_revaluation_batch_id = b.id
            left join lateral (
              select
                je.id,
                je.display_number,
                je.posted_at,
                je.created_at
              from journal_entries je
              where je.company_id = b.company_id
                and je.source_type = 'fx_revaluation'
                and je.source_id = b.id
              order by coalesce(je.posted_at, je.created_at) desc
              limit 1
            ) linked_je on true
            where b.company_id = @company_id
            group by
              b.id,
              b.entity_number,
              b.display_number,
              b.status,
              b.batch_kind,
              b.reversal_of_fx_revaluation_batch_id,
              b.company_book_id,
              b.book_code,
              b.accounting_standard,
              b.revaluation_profile,
              b.fx_rounding_policy,
              b.revaluation_date,
              b.transaction_currency_code,
              b.base_currency_code,
              b.fx_rate_snapshot_id,
              b.fx_rate,
              linked_je.id,
              linked_je.display_number,
              linked_je.posted_at,
              linked_je.created_at,
              b.created_at,
              b.updated_at
            order by coalesce(linked_je.posted_at, linked_je.created_at, b.updated_at, b.created_at) desc,
                     b.display_number desc
            limit @take;
            """
                : """
            select
              b.id,
              b.entity_number,
              b.display_number,
              b.status,
              b.batch_kind,
              b.reversal_of_fx_revaluation_batch_id,
              null::uuid as company_book_id,
              'PRIMARY'::text as book_code,
              'ASPE'::text as accounting_standard,
              'monetary_open_item_closing'::text as revaluation_profile,
              'currency_precision'::text as fx_rounding_policy,
              b.revaluation_date,
              b.transaction_currency_code,
              b.base_currency_code,
              b.fx_rate_snapshot_id,
              b.fx_rate,
              count(l.id)::int as line_count,
              coalesce(sum(l.unrealized_fx_amount), 0) as unrealized_total_base,
              linked_je.id as linked_journal_entry_id,
              linked_je.display_number as linked_journal_entry_display_number,
              linked_je.posted_at as linked_journal_posted_at,
              b.created_at,
              b.updated_at
            from fx_revaluation_batches b
            left join fx_revaluation_batch_lines l
              on l.company_id = b.company_id
             and l.fx_revaluation_batch_id = b.id
            left join lateral (
              select
                je.id,
                je.display_number,
                je.posted_at,
                je.created_at
              from journal_entries je
              where je.company_id = b.company_id
                and je.source_type = 'fx_revaluation'
                and je.source_id = b.id
              order by coalesce(je.posted_at, je.created_at) desc
              limit 1
            ) linked_je on true
            where b.company_id = @company_id
            group by
              b.id,
              b.entity_number,
              b.display_number,
              b.status,
              b.batch_kind,
              b.reversal_of_fx_revaluation_batch_id,
              b.revaluation_date,
              b.transaction_currency_code,
              b.base_currency_code,
              b.fx_rate_snapshot_id,
              b.fx_rate,
              linked_je.id,
              linked_je.display_number,
              linked_je.posted_at,
              linked_je.created_at,
              b.created_at,
              b.updated_at
            order by coalesce(linked_je.posted_at, linked_je.created_at, b.updated_at, b.created_at) desc,
                     b.display_number desc
            limit @take;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("take", effectiveTake);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new FxRevaluationBatchListItem(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("display_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetString(reader.GetOrdinal("batch_kind")),
                reader.IsDBNull(reader.GetOrdinal("reversal_of_fx_revaluation_batch_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("reversal_of_fx_revaluation_batch_id")),
                reader.IsDBNull(reader.GetOrdinal("company_book_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("company_book_id")),
                reader.IsDBNull(reader.GetOrdinal("book_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("book_code")),
                reader.IsDBNull(reader.GetOrdinal("accounting_standard"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("accounting_standard")),
                reader.IsDBNull(reader.GetOrdinal("revaluation_profile"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("revaluation_profile")),
                reader.IsDBNull(reader.GetOrdinal("fx_rounding_policy"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("fx_rounding_policy")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("revaluation_date")),
                reader.GetString(reader.GetOrdinal("transaction_currency_code")),
                reader.GetString(reader.GetOrdinal("base_currency_code")),
                reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id")),
                reader.GetDecimal(reader.GetOrdinal("fx_rate")),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetDecimal(reader.GetOrdinal("unrealized_total_base")),
                reader.IsDBNull(reader.GetOrdinal("linked_journal_entry_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("linked_journal_entry_id")),
                reader.IsDBNull(reader.GetOrdinal("linked_journal_entry_display_number"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("linked_journal_entry_display_number")),
                reader.IsDBNull(reader.GetOrdinal("linked_journal_posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("linked_journal_posted_at")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return items;
    }

    private async Task<FxRevaluationCascadeUnwindPlanResult> GetCascadeUnwindPlanCoreAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var sourceBatch = await LoadSourceBatchForUnwindAsync(
            scope,
            companyId,
            documentId,
            cancellationToken);

        if (sourceBatch.Status != "posted")
        {
            throw new InvalidOperationException("Cascade unwind plan requires a posted FX revaluation batch.");
        }

        if (sourceBatch.BatchKind != "revaluation")
        {
            throw new InvalidOperationException("Cascade unwind plan can only start from an original FX revaluation batch.");
        }

        var activeChain = await LoadActiveRevaluationChainAsync(
            scope,
            companyId,
            sourceBatch,
            cancellationToken);

        return FxRevaluationCascadePlanner.BuildPlan(
            sourceBatch.Id,
            sourceBatch.DisplayNumber,
            activeChain);
    }

    private async Task<FxRevaluationDraftPreparationResult> PrepareDraftCoreAsync(
        FxRevaluationDraftPreparation request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.IncludeAccountsReceivable && !request.IncludeAccountsPayable)
        {
            throw new InvalidOperationException("FX revaluation preparation requires at least one of AR or AP to be included.");
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(scope, request.CompanyId, cancellationToken);
        if (string.Equals(baseCurrencyCode, request.TransactionCurrencyCode.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("FX revaluation preparation requires a foreign transaction currency.");
        }

        var bookPolicy = await EnsureAndLoadRemeasurementBookPolicyAsync(
            scope,
            request.CompanyId,
            request.BookId,
            request.UserId,
            baseCurrencyCode,
            request.RevaluationDate,
            cancellationToken);

        var fxSnapshot = await LoadAcceptedFxSnapshotAsync(
            scope,
            request.CompanyId,
            baseCurrencyCode,
            request.TransactionCurrencyCode.Value,
            bookPolicy,
            request.RevaluationDate,
            request.AcceptedFxSnapshotId,
            cancellationToken);
        if (fxSnapshot is null)
        {
            throw new InvalidOperationException("No acceptable local FX snapshot was found for the requested revaluation date.");
        }

        var unrealizedFxGainAccountId = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
            scope,
            request.CompanyId,
            cancellationToken,
            "unrealized_fx_gain",
            "fx_gain_unrealized");
        var unrealizedFxLossAccountId = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
            scope,
            request.CompanyId,
            cancellationToken,
            "unrealized_fx_loss",
            "fx_loss_unrealized");

        if (!unrealizedFxGainAccountId.HasValue || !unrealizedFxLossAccountId.HasValue)
        {
            throw new InvalidOperationException(
                "FX revaluation preparation could not resolve active unrealized FX gain/loss accounts. Configure accounts.system_role or accounts.system_key with 'unrealized_fx_gain' and 'unrealized_fx_loss'.");
        }

        var candidateLines = new List<FxRevaluationPreparedLine>();
        if (request.IncludeAccountsReceivable)
        {
            candidateLines.AddRange(await LoadPreparedArLinesAsync(
                scope,
                request.CompanyId,
                baseCurrencyCode,
                request.TransactionCurrencyCode.Value,
                fxSnapshot,
                unrealizedFxGainAccountId.Value,
                unrealizedFxLossAccountId.Value,
                cancellationToken));
        }

        if (request.IncludeAccountsPayable)
        {
            candidateLines.AddRange(await LoadPreparedApLinesAsync(
                scope,
                request.CompanyId,
                baseCurrencyCode,
                request.TransactionCurrencyCode.Value,
                fxSnapshot,
                unrealizedFxGainAccountId.Value,
                unrealizedFxLossAccountId.Value,
                cancellationToken));
        }

        var preparedLines = candidateLines
            .Where(static line => line.UnrealizedFxAmount != 0m)
            .OrderBy(static line => line.TargetOpenItemType)
            .ThenBy(static line => line.Description, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (preparedLines.Length == 0)
        {
            throw new InvalidOperationException(
                "No open foreign-currency AP/AR items required revaluation for the selected date and currency.");
        }

        var batchId = Guid.NewGuid();
        var entityNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            request.CompanyId,
            $"entity-number:fx-revaluation:{request.RevaluationDate:yyyy}",
            $"EN{request.RevaluationDate:yyyy}",
            padding: 8,
            cancellationToken);
        var displayNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            request.CompanyId,
            "fx-revaluation-display",
            "FXRV-",
            padding: 6,
            cancellationToken);

        await InsertBatchHeaderAsync(
            scope,
            batchId,
            request.CompanyId,
            request.UserId,
            entityNumber,
            displayNumber,
            "revaluation",
            null,
            request.RevaluationDate,
            request.TransactionCurrencyCode.Value,
            baseCurrencyCode,
            bookPolicy,
            fxSnapshot,
            request.Memo,
            cancellationToken);
        await InsertBatchLinesAsync(scope, request.CompanyId, batchId, preparedLines, cancellationToken);

        return new FxRevaluationDraftPreparationResult(
            batchId,
            entityNumber,
            displayNumber,
            bookPolicy.BookId,
            bookPolicy.BookCode,
            bookPolicy.AccountingStandard,
            bookPolicy.RevaluationProfile,
            bookPolicy.FxRoundingPolicy,
            preparedLines.Length,
            "draft");
    }

    private async Task<FxRevaluationDraftPreparationResult> PrepareNextPeriodUnwindDraftCoreAsync(
        FxRevaluationUnwindPreparation request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var sourceBatch = await LoadSourceBatchForUnwindAsync(
            scope,
            request.CompanyId,
            request.ReversalOfDocumentId,
            cancellationToken);

        if (sourceBatch.Status != "posted")
        {
            throw new InvalidOperationException("Next-period unwind requires a posted FX revaluation batch.");
        }

        if (sourceBatch.BatchKind != "revaluation")
        {
            throw new InvalidOperationException("Next-period unwind can only be prepared from an original FX revaluation batch.");
        }

        if (request.UnwindDate <= sourceBatch.RevaluationDate)
        {
            throw new InvalidOperationException("Next-period unwind date must be later than the source revaluation date.");
        }

        FxRevaluationChainGuard.EnsureNoActiveDescendantRevaluation(
            sourceBatch.DisplayNumber,
            await FindActiveDescendantRevaluationAsync(
                scope,
                request.CompanyId,
                sourceBatch,
                cancellationToken));

        await EnsureNoActiveUnwindAsync(
            scope,
            request.CompanyId,
            request.ReversalOfDocumentId,
            cancellationToken);

        var preparedLines = await LoadPreparedUnwindLinesAsync(
            scope,
            request.CompanyId,
            sourceBatch,
            cancellationToken);

        if (preparedLines.Count == 0)
        {
            throw new InvalidOperationException("FX revaluation unwind could not find any source lines to reverse.");
        }

        var batchId = Guid.NewGuid();
        var entityNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            request.CompanyId,
            $"entity-number:fx-revaluation:{request.UnwindDate:yyyy}",
            $"EN{request.UnwindDate:yyyy}",
            padding: 8,
            cancellationToken);
        var displayNumber = await PostgresNumberingSequences.ReserveAsync(
            scope,
            request.CompanyId,
            "fx-revaluation-unwind-display",
            "FXUN-",
            padding: 6,
            cancellationToken);

        var memo = string.IsNullOrWhiteSpace(request.Memo)
            ? $"Next-period unwind of {sourceBatch.DisplayNumber}"
            : request.Memo.Trim();

        await InsertBatchHeaderAsync(
            scope,
            batchId,
            request.CompanyId,
            request.UserId,
            entityNumber,
            displayNumber,
            "next_period_unwind",
            sourceBatch.Id,
            request.UnwindDate,
            sourceBatch.TransactionCurrencyCode,
            sourceBatch.BaseCurrencyCode,
            new ResolvedRemeasurementBookPolicy(
                sourceBatch.BookId ?? Guid.Empty,
                sourceBatch.BookCode ?? "PRIMARY",
                sourceBatch.AccountingStandard ?? "ASPE",
                sourceBatch.BookBaseCurrencyCode,
                sourceBatch.FunctionalCurrencyCode,
                sourceBatch.RevaluationProfile ?? "monetary_open_item_closing",
                sourceBatch.FxRoundingPolicy ?? "currency_precision",
                sourceBatch.RateType,
                sourceBatch.QuoteBasis,
                sourceBatch.RateUseCase,
                sourceBatch.PostingReason),
            sourceBatch.FxSnapshot,
            memo,
            cancellationToken);
        await InsertBatchLinesAsync(scope, request.CompanyId, batchId, preparedLines, cancellationToken);

        return new FxRevaluationDraftPreparationResult(
            batchId,
            entityNumber,
            displayNumber,
            sourceBatch.BookId,
            sourceBatch.BookCode,
            sourceBatch.AccountingStandard,
            sourceBatch.RevaluationProfile,
            sourceBatch.FxRoundingPolicy,
            preparedLines.Count,
            "draft");
    }

    private static async Task InsertBatchHeaderAsync(
        PostgresCommandScope scope,
        Guid batchId,
        CompanyId companyId,
        UserId createdByUserId,
        string entityNumber,
        string displayNumber,
        string batchKind,
        Guid? reversalOfDocumentId,
        DateOnly revaluationDate,
        string transactionCurrencyCode,
        string baseCurrencyCode,
        ResolvedRemeasurementBookPolicy bookPolicy,
        FxSnapshotRef fxSnapshot,
        string? memo,
        CancellationToken cancellationToken)
    {
        var hasBatchGovernanceColumns = await FxBatchGovernanceColumnsExistAsync(scope, cancellationToken);
        var hasBatchRateMetadataColumns = await FxBatchRateMetadataColumnsExistAsync(scope, cancellationToken);
        var governanceColumns = hasBatchGovernanceColumns
            ? """
              company_book_id,
              book_code,
              accounting_standard,
              revaluation_profile,
              fx_rounding_policy,
              """
            : string.Empty;
        var governanceValues = hasBatchGovernanceColumns
            ? """
              @company_book_id,
              @book_code,
              @accounting_standard,
              @revaluation_profile,
              @fx_rounding_policy,
              """
            : string.Empty;
        var rateMetadataColumns = hasBatchRateMetadataColumns
            ? """
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              """
            : string.Empty;
        var rateMetadataValues = hasBatchRateMetadataColumns
            ? """
              @rate_type,
              @quote_basis,
              @rate_use_case,
              @posting_reason,
              """
            : string.Empty;

        await using var headerCommand = scope.CreateCommand(
            $"""
            insert into fx_revaluation_batches (
              id,
              company_id,
              entity_number,
              display_number,
              {governanceColumns}
              status,
              batch_kind,
              reversal_of_fx_revaluation_batch_id,
              revaluation_date,
              transaction_currency_code,
              base_currency_code,
              fx_rate_snapshot_id,
              fx_rate,
              {rateMetadataColumns}
              fx_requested_date,
              fx_effective_date,
              fx_source,
              memo,
              created_by_user_id,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @display_number,
              {governanceValues}
              'draft',
              @batch_kind,
              @reversal_of_fx_revaluation_batch_id,
              @revaluation_date,
              @transaction_currency_code,
              @base_currency_code,
              @fx_rate_snapshot_id,
              @fx_rate,
              {rateMetadataValues}
              @fx_requested_date,
              @fx_effective_date,
              @fx_source,
              @memo,
              @created_by_user_id,
              now(),
              now()
            );
            """);

        headerCommand.Parameters.AddWithValue("id", batchId);
        headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
        headerCommand.Parameters.AddWithValue("entity_number", entityNumber);
        headerCommand.Parameters.AddWithValue("display_number", displayNumber);
        headerCommand.Parameters.AddWithValue("batch_kind", batchKind);
        headerCommand.Parameters.AddWithValue(
            "reversal_of_fx_revaluation_batch_id",
            reversalOfDocumentId.HasValue ? reversalOfDocumentId.Value : DBNull.Value);
        headerCommand.Parameters.AddWithValue("revaluation_date", revaluationDate);
        headerCommand.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        headerCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        headerCommand.Parameters.AddWithValue(
            "fx_rate_snapshot_id",
            fxSnapshot.SnapshotId == Guid.Empty ? DBNull.Value : (object)fxSnapshot.SnapshotId);
        headerCommand.Parameters.AddWithValue("fx_rate", fxSnapshot.Rate);
        headerCommand.Parameters.AddWithValue("rate_type", fxSnapshot.RateType);
        headerCommand.Parameters.AddWithValue("quote_basis", fxSnapshot.QuoteBasis);
        headerCommand.Parameters.AddWithValue("rate_use_case", fxSnapshot.RateUseCase);
        headerCommand.Parameters.AddWithValue("posting_reason", fxSnapshot.PostingReason);
        headerCommand.Parameters.AddWithValue("fx_requested_date", fxSnapshot.RequestedDate);
        headerCommand.Parameters.AddWithValue("fx_effective_date", fxSnapshot.EffectiveDate);
        headerCommand.Parameters.AddWithValue("fx_source", fxSnapshot.SourceSemantics);
        headerCommand.Parameters.AddWithValue(
            "memo",
            string.IsNullOrWhiteSpace(memo)
                ? DBNull.Value
                : (object)memo.Trim());
        headerCommand.Parameters.AddWithValue("created_by_user_id", createdByUserId.Value);
        if (hasBatchGovernanceColumns)
        {
            headerCommand.Parameters.AddWithValue("company_book_id", bookPolicy.BookId == Guid.Empty ? DBNull.Value : (object)bookPolicy.BookId);
            headerCommand.Parameters.AddWithValue("book_code", bookPolicy.BookCode);
            headerCommand.Parameters.AddWithValue("accounting_standard", bookPolicy.AccountingStandard);
            headerCommand.Parameters.AddWithValue("revaluation_profile", bookPolicy.RevaluationProfile);
            headerCommand.Parameters.AddWithValue("fx_rounding_policy", bookPolicy.FxRoundingPolicy);
        }
        if (hasBatchRateMetadataColumns)
        {
            headerCommand.Parameters.AddWithValue("rate_type", fxSnapshot.RateType);
            headerCommand.Parameters.AddWithValue("quote_basis", fxSnapshot.QuoteBasis);
            headerCommand.Parameters.AddWithValue("rate_use_case", fxSnapshot.RateUseCase);
            headerCommand.Parameters.AddWithValue("posting_reason", fxSnapshot.PostingReason);
        }

        await headerCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertBatchLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid batchId,
        IReadOnlyList<FxRevaluationPreparedLine> preparedLines,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            await using var lineCommand = scope.CreateCommand(
                """
                insert into fx_revaluation_batch_lines (
                  id,
                  company_id,
                  fx_revaluation_batch_id,
                  line_number,
                  target_open_item_type,
                  target_open_item_id,
                  target_balance_side,
                  target_control_account_id,
                  offset_account_id,
                  party_id,
                  description,
                  open_amount_tx,
                  carrying_amount_base,
                  revalued_amount_base,
                  unrealized_fx_amount,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @fx_revaluation_batch_id,
                  @line_number,
                  @target_open_item_type,
                  @target_open_item_id,
                  @target_balance_side,
                  @target_control_account_id,
                  @offset_account_id,
                  @party_id,
                  @description,
                  @open_amount_tx,
                  @carrying_amount_base,
                  @revalued_amount_base,
                  @unrealized_fx_amount,
                  now(),
                  now()
                );
                """);

            lineCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            lineCommand.Parameters.AddWithValue("company_id", companyId.Value);
            lineCommand.Parameters.AddWithValue("fx_revaluation_batch_id", batchId);
            lineCommand.Parameters.AddWithValue("line_number", index + 1);
            lineCommand.Parameters.AddWithValue("target_open_item_type", line.TargetOpenItemType);
            lineCommand.Parameters.AddWithValue("target_open_item_id", line.TargetOpenItemId);
            lineCommand.Parameters.AddWithValue("target_balance_side", line.TargetBalanceSide);
            lineCommand.Parameters.AddWithValue("target_control_account_id", line.TargetControlAccountId);
            lineCommand.Parameters.AddWithValue("offset_account_id", line.OffsetAccountId);
            lineCommand.Parameters.AddWithValue("party_id", line.PartyId);
            lineCommand.Parameters.AddWithValue("description", line.Description);
            lineCommand.Parameters.AddWithValue("open_amount_tx", line.OpenAmountTx);
            lineCommand.Parameters.AddWithValue("carrying_amount_base", line.CarryingAmountBase);
            lineCommand.Parameters.AddWithValue("revalued_amount_base", line.RevaluedAmountBase);
            lineCommand.Parameters.AddWithValue("unrealized_fx_amount", line.UnrealizedFxAmount);
            await lineCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<string> LoadCompanyBaseCurrencyCodeAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select base_currency_code
            from companies
            where id = @company_id
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string baseCurrencyCode)
        {
            throw new InvalidOperationException("Active company base currency was not found.");
        }

        return baseCurrencyCode;
    }

    private static async Task<FxSnapshotRef?> LoadAcceptedFxSnapshotAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string baseCurrencyCode,
        string transactionCurrencyCode,
        ResolvedRemeasurementBookPolicy bookPolicy,
        DateOnly requestedDate,
        Guid? snapshotId,
        CancellationToken cancellationToken)
    {
        var sql = snapshotId is { } resolvedSnapshotId && resolvedSnapshotId != Guid.Empty
            ? """
              select
                s.id,
                s.base_currency_code,
                s.quote_currency_code,
                s.rate,
                s.rate_type,
                s.quote_basis,
                s.rate_use_case,
                s.posting_reason,
                s.requested_date,
                s.effective_date,
                s.snapshot_semantics
              from company_fx_rate_snapshots s
              where s.company_id = @company_id
                and s.id = @snapshot_id
                and s.base_currency_code = @base_currency_code
                and s.quote_currency_code = @transaction_currency_code
                and s.rate_type = @rate_type
                and s.quote_basis = @quote_basis
                and s.rate_use_case = @rate_use_case
                and s.posting_reason = @posting_reason
              limit 1;
              """
            : """
              select
                s.id,
                s.base_currency_code,
                s.quote_currency_code,
                s.rate,
                s.rate_type,
                s.quote_basis,
                s.rate_use_case,
                s.posting_reason,
                s.requested_date,
                s.effective_date,
                s.snapshot_semantics
              from company_fx_rate_snapshots s
              where s.company_id = @company_id
                and s.base_currency_code = @base_currency_code
                and s.quote_currency_code = @transaction_currency_code
                and s.rate_type = @rate_type
                and s.quote_basis = @quote_basis
                and s.rate_use_case = @rate_use_case
                and s.posting_reason = @posting_reason
                and s.requested_date <= @requested_date
                and s.effective_date <= @requested_date
              order by s.requested_date desc, s.effective_date desc, s.created_at desc
              limit 1;
              """;

        await using var command = scope.CreateCommand(sql);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        command.Parameters.AddWithValue("rate_type", bookPolicy.RateType);
        command.Parameters.AddWithValue("quote_basis", bookPolicy.QuoteBasis);
        command.Parameters.AddWithValue("rate_use_case", bookPolicy.RateUseCase);
        command.Parameters.AddWithValue("posting_reason", bookPolicy.PostingReason);
        command.Parameters.AddWithValue("requested_date", requestedDate);

        if (snapshotId is { } explicitSnapshotId && explicitSnapshotId != Guid.Empty)
        {
            command.Parameters.AddWithValue("snapshot_id", explicitSnapshotId);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FxSnapshotRef(
            SnapshotId: reader.GetGuid(reader.GetOrdinal("id")),
            BaseCurrencyCode: new CurrencyCode(reader.GetString(reader.GetOrdinal("base_currency_code"))),
            QuoteCurrencyCode: new CurrencyCode(reader.GetString(reader.GetOrdinal("quote_currency_code"))),
            Rate: reader.GetDecimal(reader.GetOrdinal("rate")),
            RequestedDate: reader.GetFieldValue<DateOnly>(reader.GetOrdinal("requested_date")),
            EffectiveDate: reader.GetFieldValue<DateOnly>(reader.GetOrdinal("effective_date")),
            SourceSemantics: reader.GetString(reader.GetOrdinal("snapshot_semantics")),
            RateType: reader.GetString(reader.GetOrdinal("rate_type")),
            QuoteBasis: reader.GetString(reader.GetOrdinal("quote_basis")),
            RateUseCase: reader.GetString(reader.GetOrdinal("rate_use_case")),
            PostingReason: reader.GetString(reader.GetOrdinal("posting_reason")));
    }

    private static async Task<SourceFxRevaluationBatch> LoadSourceBatchForUnwindAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var hasBatchGovernanceColumns = await FxBatchGovernanceColumnsExistAsync(scope, cancellationToken);
        var hasBatchRateMetadataColumns = await FxBatchRateMetadataColumnsExistAsync(scope, cancellationToken);
        var governanceProjection = hasBatchGovernanceColumns
            ? """
              b.company_book_id,
              b.book_code,
              b.accounting_standard,
              b.revaluation_profile,
              b.fx_rounding_policy,
              """
            : """
              null::uuid as company_book_id,
              'PRIMARY'::text as book_code,
              'ASPE'::text as accounting_standard,
              'monetary_open_item_closing'::text as revaluation_profile,
              'currency_precision'::text as fx_rounding_policy,
              """;
        var rateMetadataProjection = hasBatchRateMetadataColumns
            ? """
              b.rate_type,
              b.quote_basis,
              b.rate_use_case,
              b.posting_reason,
              """
            : """
              'closing'::text as rate_type,
              'direct'::text as quote_basis,
              'remeasurement'::text as rate_use_case,
              'revaluation'::text as posting_reason,
              """;
        await using var command = scope.CreateCommand(
            $"""
            select
              b.id,
              b.display_number,
              b.status,
              b.batch_kind,
              b.posted_at,
              {governanceProjection}
              b.revaluation_date,
              b.transaction_currency_code,
              b.base_currency_code,
              b.fx_rate_snapshot_id,
              b.fx_rate,
              {rateMetadataProjection}
              b.fx_requested_date,
              b.fx_effective_date,
              b.fx_source
            from fx_revaluation_batches b
            where b.company_id = @company_id
              and b.id = @document_id
            for update;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("FX revaluation batch was not found in the active company context.");
        }

        return new SourceFxRevaluationBatch(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("display_number")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetString(reader.GetOrdinal("batch_kind")),
            reader.IsDBNull(reader.GetOrdinal("posted_at"))
                ? (DateTimeOffset?)null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
            reader.IsDBNull(reader.GetOrdinal("company_book_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("company_book_id")),
            reader.IsDBNull(reader.GetOrdinal("book_code"))
                ? null
                : reader.GetString(reader.GetOrdinal("book_code")),
            reader.IsDBNull(reader.GetOrdinal("accounting_standard"))
                ? null
                : reader.GetString(reader.GetOrdinal("accounting_standard")),
            reader.IsDBNull(reader.GetOrdinal("revaluation_profile"))
                ? null
                : reader.GetString(reader.GetOrdinal("revaluation_profile")),
            reader.IsDBNull(reader.GetOrdinal("fx_rounding_policy"))
                ? null
                : reader.GetString(reader.GetOrdinal("fx_rounding_policy")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("revaluation_date")),
            reader.GetString(reader.GetOrdinal("transaction_currency_code")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            new FxSnapshotRef(
                SnapshotId: reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id"))
                    ? Guid.Empty
                    : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id")),
                BaseCurrencyCode: new CurrencyCode(reader.GetString(reader.GetOrdinal("base_currency_code"))),
                QuoteCurrencyCode: new CurrencyCode(reader.GetString(reader.GetOrdinal("transaction_currency_code"))),
                Rate: reader.GetDecimal(reader.GetOrdinal("fx_rate")),
                RequestedDate: reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_requested_date")),
                EffectiveDate: reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_effective_date")),
                SourceSemantics: reader.GetString(reader.GetOrdinal("fx_source")),
                RateType: reader.GetString(reader.GetOrdinal("rate_type")),
                QuoteBasis: reader.GetString(reader.GetOrdinal("quote_basis")),
                RateUseCase: reader.GetString(reader.GetOrdinal("rate_use_case")),
                PostingReason: reader.GetString(reader.GetOrdinal("posting_reason"))),
            reader.GetString(reader.GetOrdinal("rate_type")),
            reader.GetString(reader.GetOrdinal("quote_basis")),
            reader.GetString(reader.GetOrdinal("rate_use_case")),
            reader.GetString(reader.GetOrdinal("posting_reason")));
    }

    private static async Task EnsureNoActiveUnwindAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid sourceBatchId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select display_number, status
            from fx_revaluation_batches
            where company_id = @company_id
              and reversal_of_fx_revaluation_batch_id = @source_batch_id
              and status in ('draft', 'posted')
            order by created_at desc
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_batch_id", sourceBatchId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"FX revaluation batch already has an active next-period unwind draft or posting ({reader.GetString(reader.GetOrdinal("display_number"))}, status {reader.GetString(reader.GetOrdinal("status"))}).");
        }
    }

    private static async Task<IReadOnlyList<FxRevaluationCascadePlanner.ActiveRevaluationBatch>> LoadActiveRevaluationChainAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        SourceFxRevaluationBatch sourceBatch,
        CancellationToken cancellationToken)
    {
        var postedAt = sourceBatch.PostedAt ?? throw new InvalidOperationException(
            "Posted FX revaluation batch is missing posted_at.");
        var activeBatches = new List<FxRevaluationCascadePlanner.ActiveRevaluationBatch>();

        await using var command = scope.CreateCommand(
            """
            select
              descendant.id,
              descendant.display_number,
              descendant.revaluation_date,
              descendant.posted_at,
              descendant.created_at
            from fx_revaluation_batch_lines source_line
            join fx_revaluation_batch_lines descendant_line
              on descendant_line.company_id = source_line.company_id
             and descendant_line.target_open_item_type = source_line.target_open_item_type
             and descendant_line.target_open_item_id = source_line.target_open_item_id
            join fx_revaluation_batches descendant
              on descendant.company_id = descendant_line.company_id
             and descendant.id = descendant_line.fx_revaluation_batch_id
            where source_line.company_id = @company_id
              and source_line.fx_revaluation_batch_id = @source_batch_id
              and descendant.batch_kind = 'revaluation'
              and descendant.status = 'posted'
              and descendant.posted_at >= @source_posted_at
              and not exists (
                select 1
                from fx_revaluation_batches unwind
                where unwind.company_id = descendant.company_id
                  and unwind.batch_kind = 'next_period_unwind'
                  and unwind.reversal_of_fx_revaluation_batch_id = descendant.id
                  and unwind.status = 'posted'
              )
            group by
              descendant.id,
              descendant.display_number,
              descendant.revaluation_date,
              descendant.posted_at,
              descendant.created_at
            order by descendant.posted_at desc, descendant.created_at desc, descendant.id desc;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_batch_id", sourceBatch.Id);
        command.Parameters.AddWithValue("source_posted_at", postedAt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            activeBatches.Add(new FxRevaluationCascadePlanner.ActiveRevaluationBatch(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("display_number")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("revaluation_date")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"))));
        }

        return activeBatches;
    }

    private static async Task<FxRevaluationChainGuard.ActiveDescendantRevaluation?> FindActiveDescendantRevaluationAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        SourceFxRevaluationBatch sourceBatch,
        CancellationToken cancellationToken)
    {
        var activeChain = await LoadActiveRevaluationChainAsync(
            scope,
            companyId,
            sourceBatch,
            cancellationToken);
        var descendantBatch = activeChain.FirstOrDefault(batch => batch.DocumentId != sourceBatch.Id);
        if (descendantBatch is null)
        {
            return null;
        }

        await using var command = scope.CreateCommand(
            """
            select
              l.target_open_item_type,
              l.target_open_item_id
            from fx_revaluation_batch_lines l
            where l.company_id = @company_id
              and l.fx_revaluation_batch_id = @batch_id
            order by l.line_number asc
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("batch_id", descendantBatch.DocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"FX revaluation batch {descendantBatch.DisplayNumber} does not contain any lines.");
        }

        return new FxRevaluationChainGuard.ActiveDescendantRevaluation(
            descendantBatch.DocumentId,
            descendantBatch.DisplayNumber,
            reader.GetString(reader.GetOrdinal("target_open_item_type")),
            reader.GetGuid(reader.GetOrdinal("target_open_item_id")));
    }

    private static async Task<IReadOnlyList<FxRevaluationPreparedLine>> LoadPreparedUnwindLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        SourceFxRevaluationBatch sourceBatch,
        CancellationToken cancellationToken)
    {
        var lines = new List<FxRevaluationPreparedLine>();
        var lineRows = new List<FxRevaluationLineRow>();
        await using var command = scope.CreateCommand(
            """
            select
              l.line_number,
              l.target_open_item_type,
              l.target_open_item_id,
              l.target_balance_side,
              l.target_control_account_id,
              l.offset_account_id,
              l.party_id,
              l.description,
              l.open_amount_tx,
              l.carrying_amount_base,
              l.revalued_amount_base,
              l.unrealized_fx_amount
            from fx_revaluation_batch_lines l
            where l.company_id = @company_id
              and l.fx_revaluation_batch_id = @document_id
            order by l.line_number asc;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", sourceBatch.Id);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                lineRows.Add(new FxRevaluationLineRow(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetString(reader.GetOrdinal("target_open_item_type")),
                    reader.GetGuid(reader.GetOrdinal("target_open_item_id")),
                    reader.GetString(reader.GetOrdinal("target_balance_side")),
                    reader.GetGuid(reader.GetOrdinal("target_control_account_id")),
                    reader.GetGuid(reader.GetOrdinal("offset_account_id")),
                    reader.GetGuid(reader.GetOrdinal("party_id")),
                    reader.GetString(reader.GetOrdinal("description")),
                    reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
                    reader.GetDecimal(reader.GetOrdinal("carrying_amount_base")),
                    reader.GetDecimal(reader.GetOrdinal("revalued_amount_base")),
                    reader.GetDecimal(reader.GetOrdinal("unrealized_fx_amount")),
                    null));
            }
        }

        foreach (var lineRow in lineRows)
        {
            var currentOpenItem = await LoadCurrentOpenItemAsync(
                scope,
                companyId,
                lineRow.TargetOpenItemType,
                lineRow.TargetOpenItemId,
                cancellationToken);

            if (currentOpenItem.PartyId != lineRow.PartyId)
            {
                throw new InvalidOperationException(
                    $"FX revaluation unwind line {lineRow.LineNumber} no longer matches the target party.");
            }

            var targetBalanceSide = lineRow.TargetBalanceSide;
            if (!string.Equals(currentOpenItem.BalanceSide, targetBalanceSide, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"FX revaluation unwind line {lineRow.LineNumber} cannot be prepared because the target balance side changed outside the supported flow.");
            }

            if (currentOpenItem.Status == "voided")
            {
                throw new InvalidOperationException(
                    $"FX revaluation unwind line {lineRow.LineNumber} targets an open item that is voided.");
            }

            var originalOpenAmountTx = lineRow.OpenAmountTx;
            var sourceCarryingAmountBase = lineRow.CarryingAmountBase;
            var sourceRevaluedAmountBase = lineRow.RevaluedAmountBase;
            var settlementSequence = await LoadSettlementSequenceAsync(
                scope,
                companyId,
                lineRow.TargetOpenItemType,
                lineRow.TargetOpenItemId,
                sourceBatch.PostedAt ?? throw new InvalidOperationException(
                    "Posted FX revaluation batch is missing posted_at."),
                cancellationToken);
            var expectedRemainingRevalued = FxRevaluationUnwindMath.ReplayRemainingState(
                originalOpenAmountTx,
                sourceRevaluedAmountBase,
                settlementSequence);
            var expectedRemainingCarrying = FxRevaluationUnwindMath.ReplayRemainingState(
                originalOpenAmountTx,
                sourceCarryingAmountBase,
                settlementSequence);

            if (Round6(currentOpenItem.OpenAmountTx) != Round6(expectedRemainingRevalued.OpenAmountTx))
            {
                throw new InvalidOperationException(
                    $"FX revaluation unwind line {lineRow.LineNumber} cannot be prepared because the target open amount changed outside the supported settlement flow.");
            }

            if (Round6(currentOpenItem.OpenAmountBase) != Round6(expectedRemainingRevalued.OpenAmountBase))
            {
                throw new InvalidOperationException(
                    $"FX revaluation unwind line {lineRow.LineNumber} cannot be prepared because the target carrying base changed outside the supported settlement flow.");
            }

            if (currentOpenItem.OpenAmountTx == 0m || currentOpenItem.Status == "closed")
            {
                continue;
            }

            var unwindAmountBase = Round6(
                expectedRemainingCarrying.OpenAmountBase - expectedRemainingRevalued.OpenAmountBase);
            if (unwindAmountBase == 0m)
            {
                continue;
            }

            lines.Add(new FxRevaluationPreparedLine(
                lineRow.LineNumber,
                lineRow.TargetOpenItemType,
                lineRow.TargetOpenItemId,
                targetBalanceSide,
                lineRow.TargetControlAccountId,
                lineRow.OffsetAccountId,
                lineRow.PartyId,
                $"Next-period unwind of {sourceBatch.DisplayNumber} line {lineRow.LineNumber}: {lineRow.Description}",
                expectedRemainingRevalued.OpenAmountTx,
                expectedRemainingRevalued.OpenAmountBase,
                expectedRemainingCarrying.OpenAmountBase,
                unwindAmountBase));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<decimal>> LoadSettlementSequenceAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string targetOpenItemType,
        Guid targetOpenItemId,
        DateTimeOffset postedAt,
        CancellationToken cancellationToken)
    {
        var appliedAmountsTx = new List<decimal>();
        await using var command = scope.CreateCommand(
            """
            select applied_amount_tx
            from settlement_applications
            where company_id = @company_id
              and target_open_item_type = @target_open_item_type
              and target_open_item_id = @target_open_item_id
              and created_at >= @posted_at
            order by created_at asc, id asc;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("target_open_item_type", targetOpenItemType);
        command.Parameters.AddWithValue("target_open_item_id", targetOpenItemId);
        command.Parameters.AddWithValue("posted_at", postedAt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            appliedAmountsTx.Add(reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")));
        }

        return appliedAmountsTx;
    }

    private static async Task<IReadOnlyList<FxRevaluationPreparedLine>> LoadPreparedArLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string baseCurrencyCode,
        string transactionCurrencyCode,
        FxSnapshotRef fxSnapshot,
        Guid unrealizedFxGainAccountId,
        Guid unrealizedFxLossAccountId,
        CancellationToken cancellationToken)
    {
        var controlAccountId = await PostgresControlAccountLookup.TryResolveAsync(
            scope,
            companyId,
            "accounts_receivable",
            transactionCurrencyCode,
            baseCurrencyCode,
            cancellationToken);
        if (!controlAccountId.HasValue)
        {
            throw new InvalidOperationException(
                "FX revaluation could not resolve an active foreign-currency Accounts Receivable control account.");
        }

        var lines = new List<FxRevaluationPreparedLine>();
        await using var command = scope.CreateCommand(
            """
            select
              id,
              customer_id,
              source_type,
              source_id,
              balance_side,
              open_amount_tx,
              open_amount_base
            from ar_open_items
            where company_id = @company_id
              and document_currency_code = @transaction_currency_code
              and base_currency_code = @base_currency_code
              and status in ('open', 'partially_applied')
              and open_amount_tx > 0
              and open_amount_base > 0
            order by due_date asc nulls first, created_at asc, id asc;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var openAmountTx = reader.GetDecimal(reader.GetOrdinal("open_amount_tx"));
            var carryingAmountBase = reader.GetDecimal(reader.GetOrdinal("open_amount_base"));
            var revaluedAmountBase = SettlementAmountMath.RoundBase(openAmountTx * fxSnapshot.Rate);
            var unrealizedAmountBase = SettlementAmountMath.RoundBase(revaluedAmountBase - carryingAmountBase);
            if (unrealizedAmountBase == 0m)
            {
                continue;
            }

            var sourceType = reader.GetString(reader.GetOrdinal("source_type"));
            var sourceId = reader.GetGuid(reader.GetOrdinal("source_id"));
            var balanceSide = reader.GetString(reader.GetOrdinal("balance_side"));
            lines.Add(new FxRevaluationPreparedLine(
                lines.Count + 1,
                "ar_open_item",
                reader.GetGuid(reader.GetOrdinal("id")),
                balanceSide,
                controlAccountId.Value,
                ResolveOffsetAccountId(
                    balanceSide,
                    unrealizedAmountBase,
                    unrealizedFxGainAccountId,
                    unrealizedFxLossAccountId),
                reader.GetGuid(reader.GetOrdinal("customer_id")),
                $"AR FX revaluation for {sourceType} {sourceId}",
                openAmountTx,
                carryingAmountBase,
                revaluedAmountBase,
                unrealizedAmountBase));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<FxRevaluationPreparedLine>> LoadPreparedApLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string baseCurrencyCode,
        string transactionCurrencyCode,
        FxSnapshotRef fxSnapshot,
        Guid unrealizedFxGainAccountId,
        Guid unrealizedFxLossAccountId,
        CancellationToken cancellationToken)
    {
        var controlAccountId = await PostgresControlAccountLookup.TryResolveAsync(
            scope,
            companyId,
            "accounts_payable",
            transactionCurrencyCode,
            baseCurrencyCode,
            cancellationToken);
        if (!controlAccountId.HasValue)
        {
            throw new InvalidOperationException(
                "FX revaluation could not resolve an active foreign-currency Accounts Payable control account.");
        }

        var lines = new List<FxRevaluationPreparedLine>();
        await using var command = scope.CreateCommand(
            """
            select
              id,
              vendor_id,
              source_type,
              source_id,
              balance_side,
              open_amount_tx,
              open_amount_base
            from ap_open_items
            where company_id = @company_id
              and document_currency_code = @transaction_currency_code
              and base_currency_code = @base_currency_code
              and status in ('open', 'partially_applied')
              and open_amount_tx > 0
              and open_amount_base > 0
            order by due_date asc nulls first, created_at asc, id asc;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var openAmountTx = reader.GetDecimal(reader.GetOrdinal("open_amount_tx"));
            var carryingAmountBase = reader.GetDecimal(reader.GetOrdinal("open_amount_base"));
            var revaluedAmountBase = SettlementAmountMath.RoundBase(openAmountTx * fxSnapshot.Rate);
            var unrealizedAmountBase = SettlementAmountMath.RoundBase(revaluedAmountBase - carryingAmountBase);
            if (unrealizedAmountBase == 0m)
            {
                continue;
            }

            var sourceType = reader.GetString(reader.GetOrdinal("source_type"));
            var sourceId = reader.GetGuid(reader.GetOrdinal("source_id"));
            var balanceSide = reader.GetString(reader.GetOrdinal("balance_side"));
            lines.Add(new FxRevaluationPreparedLine(
                lines.Count + 1,
                "ap_open_item",
                reader.GetGuid(reader.GetOrdinal("id")),
                balanceSide,
                controlAccountId.Value,
                ResolveOffsetAccountId(
                    balanceSide,
                    unrealizedAmountBase,
                    unrealizedFxGainAccountId,
                    unrealizedFxLossAccountId),
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                $"AP FX revaluation for {sourceType} {sourceId}",
                openAmountTx,
                carryingAmountBase,
                revaluedAmountBase,
                unrealizedAmountBase));
        }

        return lines;
    }

    private static async Task<CurrentOpenItemState> LoadCurrentOpenItemAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string targetOpenItemType,
        Guid targetOpenItemId,
        CancellationToken cancellationToken)
    {
        var (tableName, partyColumn) = targetOpenItemType switch
        {
            "ar_open_item" => ("ar_open_items", "customer_id"),
            "ap_open_item" => ("ap_open_items", "vendor_id"),
            _ => throw new InvalidOperationException(
                $"FX revaluation line target type '{targetOpenItemType}' is not supported.")
        };

        await using var command = scope.CreateCommand(
            $"""
            select
              {partyColumn},
              balance_side,
              open_amount_tx,
              open_amount_base,
              status
            from {tableName}
            where company_id = @company_id
              and id = @target_open_item_id
            for update;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("target_open_item_id", targetOpenItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("FX revaluation batch references an open item that does not exist.");
        }

        return new CurrentOpenItemState(
            reader.GetGuid(reader.GetOrdinal(partyColumn)),
            reader.GetString(reader.GetOrdinal("balance_side")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_base")),
            reader.GetString(reader.GetOrdinal("status")));
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static Guid ResolveOffsetAccountId(
        string targetBalanceSide,
        decimal unrealizedAmountBase,
        Guid unrealizedFxGainAccountId,
        Guid unrealizedFxLossAccountId) =>
        targetBalanceSide switch
        {
            "debit" when unrealizedAmountBase > 0m => unrealizedFxGainAccountId,
            "debit" => unrealizedFxLossAccountId,
            "credit" when unrealizedAmountBase > 0m => unrealizedFxLossAccountId,
            "credit" => unrealizedFxGainAccountId,
            _ => throw new InvalidOperationException(
                $"FX revaluation line balance side '{targetBalanceSide}' is not supported.")
        };

    private sealed record FxRevaluationLineRow(
        int LineNumber,
        string TargetOpenItemType,
        Guid TargetOpenItemId,
        string TargetBalanceSide,
        Guid TargetControlAccountId,
        Guid OffsetAccountId,
        Guid PartyId,
        string Description,
        decimal OpenAmountTx,
        decimal CarryingAmountBase,
        decimal RevaluedAmountBase,
        decimal UnrealizedFxAmount,
        DateTimeOffset? AppliedAt);

    private sealed record FxRevaluationPreparedLine(
        int LineNumber,
        string TargetOpenItemType,
        Guid TargetOpenItemId,
        string TargetBalanceSide,
        Guid TargetControlAccountId,
        Guid OffsetAccountId,
        Guid PartyId,
        string Description,
        decimal OpenAmountTx,
        decimal CarryingAmountBase,
        decimal RevaluedAmountBase,
        decimal UnrealizedFxAmount);

    private sealed record CurrentOpenItemState(
        Guid PartyId,
        string BalanceSide,
        decimal OpenAmountTx,
        decimal OpenAmountBase,
        string Status);

    private static async Task<ResolvedRemeasurementBookPolicy> EnsureAndLoadRemeasurementBookPolicyAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid? requestedBookId,
        UserId userId,
        string companyBaseCurrencyCode,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        if (!await BookGovernanceTablesExistAsync(scope, cancellationToken))
        {
            if (requestedBookId.HasValue)
            {
                throw new InvalidOperationException(
                    "FX revaluation book selection requires company book governance tables to be installed.");
            }

            return BuildFallbackPrimaryBookPolicy(companyBaseCurrencyCode);
        }

        if (!requestedBookId.HasValue)
        {
            await EnsureDefaultPrimaryBookPolicyAsync(
                scope,
                companyId,
                userId,
                companyBaseCurrencyCode,
                asOfDate,
                cancellationToken);
        }

        var policy = await LoadRemeasurementBookPolicyAsync(
            scope,
            companyId,
            requestedBookId,
            asOfDate,
            cancellationToken);

        if (policy is null)
        {
            throw new InvalidOperationException(
                requestedBookId.HasValue
                    ? $"FX revaluation book {requestedBookId.Value:D} does not have an active governed remeasurement policy."
                    : "FX revaluation requires an active governed primary book remeasurement policy.");
        }

        if (!string.Equals(policy.BookBaseCurrencyCode, companyBaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"FX revaluation currently supports only books whose base currency matches the active company base currency {companyBaseCurrencyCode}. Book {policy.BookCode} uses {policy.BookBaseCurrencyCode}.");
        }

        return policy;
    }

    private static async Task<bool> BookGovernanceTablesExistAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select count(*)
            from information_schema.tables
            where table_schema = current_schema()
              and table_name in ('company_books', 'company_book_remeasurement_policies');
            """);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 2;
    }

    private static async Task<bool> FxBatchGovernanceColumnsExistAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select count(*)
            from information_schema.columns
            where table_schema = current_schema()
              and table_name = 'fx_revaluation_batches'
              and column_name in ('company_book_id', 'book_code', 'accounting_standard', 'revaluation_profile', 'fx_rounding_policy');
            """);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 5;
    }

    private static async Task<bool> FxBatchRateMetadataColumnsExistAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select count(*)
            from information_schema.columns
            where table_schema = current_schema()
              and table_name = 'fx_revaluation_batches'
              and column_name in ('rate_type', 'quote_basis', 'rate_use_case', 'posting_reason');
            """);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 4;
    }

    private static ResolvedRemeasurementBookPolicy BuildFallbackPrimaryBookPolicy(string companyBaseCurrencyCode) =>
        new(
            Guid.Empty,
            "PRIMARY",
            "ASPE",
            companyBaseCurrencyCode,
            companyBaseCurrencyCode,
            "monetary_open_item_closing",
            "currency_precision",
            "closing",
            "direct",
            "remeasurement",
            "revaluation");

    private static async Task EnsureDefaultPrimaryBookPolicyAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        UserId userId,
        string companyBaseCurrencyCode,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var existing = await LoadRemeasurementBookPolicyAsync(
            scope,
            companyId,
            bookId: null,
            asOfDate,
            cancellationToken);
        if (existing is not null)
        {
            return;
        }

        await using var insertBookCommand = scope.CreateCommand(
            """
            insert into company_books (
              id,
              company_id,
              book_code,
              book_name,
              book_role,
              accounting_standard,
              book_base_currency_code,
              functional_currency_code,
              presentation_currency_code,
              is_primary,
              is_adjustment_only,
              effective_from,
              is_active,
              created_by_user_id,
              created_at,
              updated_at
            )
            select
              gen_random_uuid(),
              @company_id,
              'PRIMARY',
              'Primary Book',
              'primary',
              'ASPE',
              @book_base_currency_code,
              @functional_currency_code,
              null,
              true,
              false,
              @effective_from,
              true,
              @created_by_user_id,
              now(),
              now()
            where not exists (
              select 1
              from company_books b
              where b.company_id = @company_id
                and b.is_active = true
                and b.is_primary = true
            );
            """);
        insertBookCommand.Parameters.AddWithValue("company_id", companyId.Value);
        insertBookCommand.Parameters.AddWithValue("book_base_currency_code", companyBaseCurrencyCode);
        insertBookCommand.Parameters.AddWithValue("functional_currency_code", companyBaseCurrencyCode);
        insertBookCommand.Parameters.AddWithValue("effective_from", asOfDate);
        insertBookCommand.Parameters.AddWithValue("created_by_user_id", userId.Value);
        await insertBookCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var insertPolicyCommand = scope.CreateCommand(
            """
            insert into company_book_remeasurement_policies (
              id,
              company_id,
              company_book_id,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              revaluation_profile,
              fx_rounding_policy,
              effective_from,
              is_active,
              created_by_user_id,
              created_at,
              updated_at
            )
            select
              gen_random_uuid(),
              @company_id,
              b.id,
              'closing',
              'direct',
              'remeasurement',
              'revaluation',
              'monetary_open_item_closing',
              'currency_precision',
              @effective_from,
              true,
              @created_by_user_id,
              now(),
              now()
            from company_books b
            where b.company_id = @company_id
              and b.is_active = true
              and b.is_primary = true
              and not exists (
                select 1
                from company_book_remeasurement_policies p
                where p.company_id = b.company_id
                  and p.company_book_id = b.id
                  and p.is_active = true
              )
            order by b.effective_from desc, b.created_at desc, b.id desc
            limit 1;
            """);
        insertPolicyCommand.Parameters.AddWithValue("company_id", companyId.Value);
        insertPolicyCommand.Parameters.AddWithValue("effective_from", asOfDate);
        insertPolicyCommand.Parameters.AddWithValue("created_by_user_id", userId.Value);
        await insertPolicyCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ResolvedRemeasurementBookPolicy?> LoadRemeasurementBookPolicyAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid? bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              b.id as company_book_id,
              b.book_code,
              b.accounting_standard,
              b.book_base_currency_code,
              b.functional_currency_code,
              p.revaluation_profile,
              p.fx_rounding_policy,
              p.rate_type,
              p.quote_basis,
              p.rate_use_case,
              p.posting_reason
            from company_books b
            join company_book_remeasurement_policies p
              on p.company_id = b.company_id
             and p.company_book_id = b.id
            where b.company_id = @company_id
              and b.is_active = true
              and p.is_active = true
              and b.effective_from <= @as_of_date
              and p.effective_from <= @as_of_date
              and (@book_id::uuid is null or b.id = @book_id)
              and (@book_id::uuid is not null or b.is_primary = true)
            order by
              p.effective_from desc,
              b.effective_from desc,
              p.created_at desc,
              b.created_at desc,
              p.id desc,
              b.id desc
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("book_id", bookId.HasValue ? bookId.Value : DBNull.Value);
        command.Parameters.AddWithValue("as_of_date", asOfDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ResolvedRemeasurementBookPolicy(
            reader.GetGuid(reader.GetOrdinal("company_book_id")),
            reader.GetString(reader.GetOrdinal("book_code")),
            reader.GetString(reader.GetOrdinal("accounting_standard")),
            reader.GetString(reader.GetOrdinal("book_base_currency_code")),
            reader.GetString(reader.GetOrdinal("functional_currency_code")),
            reader.GetString(reader.GetOrdinal("revaluation_profile")),
            reader.GetString(reader.GetOrdinal("fx_rounding_policy")),
            reader.GetString(reader.GetOrdinal("rate_type")),
            reader.GetString(reader.GetOrdinal("quote_basis")),
            reader.GetString(reader.GetOrdinal("rate_use_case")),
            reader.GetString(reader.GetOrdinal("posting_reason")));
    }

    private sealed record SourceFxRevaluationBatch(
        Guid Id,
        string DisplayNumber,
        string Status,
        string BatchKind,
        DateTimeOffset? PostedAt,
        Guid? BookId,
        string? BookCode,
        string? AccountingStandard,
        string? RevaluationProfile,
        string? FxRoundingPolicy,
        DateOnly RevaluationDate,
        string TransactionCurrencyCode,
        string BaseCurrencyCode,
        string BookBaseCurrencyCode,
        string FunctionalCurrencyCode,
        FxSnapshotRef FxSnapshot,
        string RateType,
        string QuoteBasis,
        string RateUseCase,
        string PostingReason);

    private sealed record ResolvedRemeasurementBookPolicy(
        Guid BookId,
        string BookCode,
        string AccountingStandard,
        string BookBaseCurrencyCode,
        string FunctionalCurrencyCode,
        string RevaluationProfile,
        string FxRoundingPolicy,
        string RateType,
        string QuoteBasis,
        string RateUseCase,
        string PostingReason);
}
