using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Npgsql;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// Tax return repository — single-row document, no lines.
///
/// At <see cref="GetForPostingAsync"/> time we resolve all five
/// required tax accounts by canonical chart code. V1 codes (QBO-
/// flavoured Canada GST/HST setup):
///   • 21000 GST/HST Payable               → tax_payable
///   • 13500 GST/HST Receivable            → tax_receivable
///   • 21001 GST/HST Adjustments           → tax_adjustments
///   • 21002 GST/HST Filing Liability       → tax_filing_liability
///   • 13501 GST/HST Filing Receivable      → tax_filing_receivable
/// Future iteration moves these to company_settings.tax_regime_*
/// columns so a multi-regime company (GST + QST) can configure each
/// regime's own quintet.
///
/// The <see cref="net_amount"/> column on tax_returns stores the
/// signed Net (Collected − ITCs + Adjustments) so reports don't
/// recompute. The Domain constructor + the BuildTaxReturnFragments
/// method use the same arithmetic.
/// </summary>
public sealed class PostgresTaxReturnDocumentRepository : ITaxReturnDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresTaxReturnDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<TaxReturnDocument?> GetForPostingAsync(
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
        string returnNumber;
        string status;
        string taxRegime;
        string filingFrequency;
        DateOnly periodStart;
        DateOnly periodEnd;
        string baseCurrencyCode;
        decimal collected;
        decimal inputCredits;
        decimal adjustments;
        string? adjustmentsNote;
        decimal net;
        string? regulatorReferenceNo;
        string? memo;

        await using (var headerCommand = scope.CreateCommand(
                         """
                         select
                           tr.id,
                           tr.entity_number,
                           tr.return_number,
                           tr.status,
                           tr.tax_regime,
                           tr.filing_frequency,
                           tr.period_start,
                           tr.period_end,
                           tr.base_currency_code,
                           tr.collected_amount,
                           tr.input_credits_amount,
                           tr.adjustments_amount,
                           tr.adjustments_note,
                           tr.net_amount,
                           tr.regulator_reference_no,
                           tr.memo
                         from tax_returns tr
                         where tr.company_id = @company_id
                           and tr.id = @document_id
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
            returnNumber = reader.GetString(reader.GetOrdinal("return_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            taxRegime = reader.GetString(reader.GetOrdinal("tax_regime"));
            filingFrequency = reader.GetString(reader.GetOrdinal("filing_frequency"));
            periodStart = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("period_start"));
            periodEnd = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("period_end"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            collected = reader.GetDecimal(reader.GetOrdinal("collected_amount"));
            inputCredits = reader.GetDecimal(reader.GetOrdinal("input_credits_amount"));
            adjustments = reader.GetDecimal(reader.GetOrdinal("adjustments_amount"));
            adjustmentsNote = reader.IsDBNull(reader.GetOrdinal("adjustments_note"))
                ? null
                : reader.GetString(reader.GetOrdinal("adjustments_note"));
            net = reader.GetDecimal(reader.GetOrdinal("net_amount"));
            regulatorReferenceNo = reader.IsDBNull(reader.GetOrdinal("regulator_reference_no"))
                ? null
                : reader.GetString(reader.GetOrdinal("regulator_reference_no"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
        }

        // Resolve the five required tax accounts. Missing any of them
        // is a setup bug — V1 surfaces it as an InvalidOperationException
        // so the operator sees actionable feedback ("create code 21000
        // GST/HST Payable on the chart").
        var taxPayableAccountId = await ResolveAccountByCodeAsync(scope, companyId, "21000", "GST/HST Payable", cancellationToken);
        var taxReceivableAccountId = await ResolveAccountByCodeAsync(scope, companyId, "13500", "GST/HST Receivable", cancellationToken);
        var taxAdjustmentsAccountId = await ResolveAccountByCodeAsync(scope, companyId, "21001", "GST/HST Adjustments", cancellationToken);
        var taxFilingLiabilityAccountId = await ResolveAccountByCodeAsync(scope, companyId, "21002", "GST/HST Filing Liability", cancellationToken);
        var taxFilingReceivableAccountId = await ResolveAccountByCodeAsync(scope, companyId, "13501", "GST/HST Filing Receivable", cancellationToken);

        return new TaxReturnDocument(
            id,
            companyId,
            new EntityNumber(entityNumber),
            new DocumentNumber(returnNumber),
            status,
            taxRegime,
            filingFrequency,
            periodStart,
            periodEnd,
            new CurrencyCode(baseCurrencyCode),
            collected,
            inputCredits,
            adjustments,
            net,
            adjustmentsNote,
            regulatorReferenceNo,
            taxPayableAccountId,
            taxReceivableAccountId,
            taxAdjustmentsAccountId,
            taxFilingLiabilityAccountId,
            taxFilingReceivableAccountId,
            memo);
    }

    private static async Task<Guid> ResolveAccountByCodeAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string code,
        string canonicalName,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id
            from accounts
            where company_id = @company_id
              and code = @code
              and is_active = true
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("code", code);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            throw new InvalidOperationException(
                $"Tax return posting requires an active '{canonicalName}' account (code {code}) on the chart of accounts.");
        }
        return (Guid)result;
    }

    public async Task<IReadOnlyList<TaxReturnListItem>> ListAsync(
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
              select tr.id, tr.entity_number, tr.return_number, tr.status, tr.tax_regime, tr.filing_frequency,
                     tr.period_start, tr.period_end, tr.net_amount, tr.base_currency_code, tr.posted_at
              from tax_returns tr
              where tr.company_id = @company_id
              order by tr.period_end desc, tr.created_at desc
              limit 200;
              """
            : """
              select tr.id, tr.entity_number, tr.return_number, tr.status, tr.tax_regime, tr.filing_frequency,
                     tr.period_start, tr.period_end, tr.net_amount, tr.base_currency_code, tr.posted_at
              from tax_returns tr
              where tr.company_id = @company_id
                and tr.status <> 'draft'
              order by tr.period_end desc, tr.created_at desc
              limit 200;
              """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var rows = new List<TaxReturnListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TaxReturnListItem(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("return_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetString(reader.GetOrdinal("tax_regime")),
                reader.GetString(reader.GetOrdinal("filing_frequency")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("period_start")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("period_end")),
                reader.GetDecimal(reader.GetOrdinal("net_amount")),
                reader.GetString(reader.GetOrdinal("base_currency_code")),
                reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"))));
        }
        return rows;
    }

    public async Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        TaxReturnDraftSaveModel draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var documentId = draft.DocumentId ?? Guid.NewGuid();
        var net = Round6(draft.CollectedAmount - draft.InputCreditsAmount + draft.AdjustmentsAmount);
        string entityNumber;
        string returnNumber;

        if (draft.DocumentId is null)
        {
            var year = draft.PeriodEnd.Year;
            entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId.Value,
                $"entity-number:all:{year}",
                $"EN{year}",
                8,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(
                    connection, transaction, year, cancellationToken),
                cancellationToken);

            returnNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId.Value,
                "tax-return-display",
                "TR-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId.Value,
                    "tax_returns",
                    "return_number",
                    "^TR-[0-9]+$",
                    4,
                    cancellationToken),
                cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into tax_returns (
                  id,
                  company_id,
                  entity_number,
                  return_number,
                  status,
                  tax_regime,
                  filing_frequency,
                  period_start,
                  period_end,
                  base_currency_code,
                  collected_amount,
                  input_credits_amount,
                  adjustments_amount,
                  adjustments_note,
                  net_amount,
                  regulator_reference_no,
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
                  @return_number,
                  'draft',
                  @tax_regime,
                  @filing_frequency,
                  @period_start,
                  @period_end,
                  @base_currency_code,
                  @collected_amount,
                  @input_credits_amount,
                  @adjustments_amount,
                  @adjustments_note,
                  @net_amount,
                  @regulator_reference_no,
                  @memo,
                  null,
                  @created_by_user_id,
                  now(),
                  now()
                );
                """;
            BindHeader(insertCommand, draft, documentId, entityNumber, returnNumber, net);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            (entityNumber, returnNumber) = await LoadIdentityAsync(
                connection, transaction, draft.CompanyId.Value, documentId, cancellationToken);

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update tax_returns
                set tax_regime = @tax_regime,
                    filing_frequency = @filing_frequency,
                    period_start = @period_start,
                    period_end = @period_end,
                    base_currency_code = @base_currency_code,
                    collected_amount = @collected_amount,
                    input_credits_amount = @input_credits_amount,
                    adjustments_amount = @adjustments_amount,
                    adjustments_note = @adjustments_note,
                    net_amount = @net_amount,
                    regulator_reference_no = @regulator_reference_no,
                    memo = @memo,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = 'draft';
                """;
            BindHeader(updateCommand, draft, documentId, entityNumber, returnNumber, net, includeIdentity: false);
            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "The tax return draft could not be updated. Only draft returns can be modified.");
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, returnNumber, "draft");
    }

    private static void ValidateDraft(TaxReturnDraftSaveModel draft)
    {
        if (string.IsNullOrWhiteSpace(draft.TaxRegime))
        {
            throw new InvalidOperationException("Tax return requires a tax regime.");
        }
        if (string.IsNullOrWhiteSpace(draft.FilingFrequency))
        {
            throw new InvalidOperationException("Tax return requires a filing frequency.");
        }
        if (draft.PeriodEnd < draft.PeriodStart)
        {
            throw new InvalidOperationException("Tax return period end must be on or after period start.");
        }
        if (string.IsNullOrWhiteSpace(draft.BaseCurrencyCode))
        {
            throw new InvalidOperationException("Tax return requires a base currency code.");
        }
        if (draft.CollectedAmount < 0m || draft.InputCreditsAmount < 0m)
        {
            throw new InvalidOperationException("Tax return collected and ITC amounts must be non-negative.");
        }
    }

    private static void BindHeader(
        NpgsqlCommand command,
        TaxReturnDraftSaveModel draft,
        Guid documentId,
        string entityNumber,
        string returnNumber,
        decimal net,
        bool includeIdentity = true)
    {
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("return_number", returnNumber);
            command.Parameters.AddWithValue("created_by_user_id", draft.UserId.Value);
        }

        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
        command.Parameters.AddWithValue("tax_regime", draft.TaxRegime.Trim());
        command.Parameters.AddWithValue("filing_frequency", draft.FilingFrequency.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("period_start", draft.PeriodStart);
        command.Parameters.AddWithValue("period_end", draft.PeriodEnd);
        command.Parameters.AddWithValue("base_currency_code", draft.BaseCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("collected_amount", Round6(draft.CollectedAmount));
        command.Parameters.AddWithValue("input_credits_amount", Round6(draft.InputCreditsAmount));
        command.Parameters.AddWithValue("adjustments_amount", Round6(draft.AdjustmentsAmount));
        command.Parameters.AddWithValue("adjustments_note", string.IsNullOrWhiteSpace(draft.AdjustmentsNote) ? (object)DBNull.Value : draft.AdjustmentsNote.Trim());
        command.Parameters.AddWithValue("net_amount", net);
        command.Parameters.AddWithValue("regulator_reference_no", string.IsNullOrWhiteSpace(draft.RegulatorReferenceNo) ? (object)DBNull.Value : draft.RegulatorReferenceNo.Trim());
        command.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(draft.Memo) ? (object)DBNull.Value : draft.Memo.Trim());
    }

    private static async Task<(string EntityNumber, string ReturnNumber)> LoadIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select entity_number, return_number
            from tax_returns
            where id = @id and company_id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Tax return draft not found in the active company context.");
        }

        return (
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("return_number")));
    }

    private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.ToEven);
}
