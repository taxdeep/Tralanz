using Modules.Company.MultiBook;
using Npgsql;
using NpgsqlTypes;
using SharedKernel.Company;

namespace Infrastructure.PostgreSQL.Company;

public sealed class PostgreSqlCompanyBookPolicyStore : ICompanyBookPolicyStore
{
    private const string DefaultBookCode = "PRIMARY";
    private const string DefaultBookName = "Primary Book";
    private const string DefaultBookRole = "primary";
    private const string DefaultAccountingStandard = "ASPE";
    private const string DefaultRateType = "closing";
    private const string DefaultQuoteBasis = "direct";
    private const string DefaultRateUseCase = "remeasurement";
    private const string DefaultPostingReason = "revaluation";
    private const string DefaultRevaluationProfile = "monetary_open_item_closing";
    private const string DefaultFxRoundingPolicy = "currency_precision";

    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlCompanyBookPolicyStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<IReadOnlyList<CompanyBookGovernanceState>> ListBookGovernanceAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              b.id as book_id,
              b.company_id,
              b.book_code,
              b.book_name,
              b.book_role,
              b.accounting_standard,
              b.book_base_currency_code,
              b.functional_currency_code,
              b.presentation_currency_code,
              b.is_primary,
              b.is_adjustment_only,
              b.effective_from as book_effective_from,
              b.is_active as book_is_active,
              p.id as policy_id,
              p.rate_type,
              p.quote_basis,
              p.rate_use_case,
              p.posting_reason,
              p.revaluation_profile,
              p.fx_rounding_policy,
              p.effective_from as policy_effective_from,
              p.is_active as policy_is_active,
              exists (
                select 1
                from journal_entries je
                where je.company_id = @company_id
                  and je.status = 'posted'
              ) as has_company_posted_history,
              exists (
                select 1
                from fx_revaluation_batches frb
                where frb.company_id = @company_id
                  and frb.company_book_id = b.id
                  and frb.status = 'posted'
              ) as has_book_revaluation_history,
              exists (
                select 1
                from company_book_governance_signals s
                where s.company_id = @company_id
                  and s.company_book_id = b.id
                  and s.signal_type = 'closed_period'
                  and s.signal_date <= @as_of_date
              ) as has_closed_periods,
              exists (
                select 1
                from company_book_governance_signals s
                where s.company_id = @company_id
                  and s.company_book_id = b.id
                  and s.signal_type = 'reported_statement'
                  and s.signal_date <= @as_of_date
              ) as has_issued_reports,
              exists (
                select 1
                from company_book_governance_signals s
                where s.company_id = @company_id
                  and s.company_book_id = b.id
                  and s.signal_type = 'filed_tax'
                  and s.signal_date <= @as_of_date
              ) as has_filed_tax
            from company_books b
            left join lateral (
              select
                p.id,
                p.rate_type,
                p.quote_basis,
                p.rate_use_case,
                p.posting_reason,
                p.revaluation_profile,
                p.fx_rounding_policy,
                p.effective_from,
                p.is_active
              from company_book_remeasurement_policies p
              where p.company_id = b.company_id
                and p.company_book_id = b.id
                and p.is_active = true
                and p.effective_from <= @as_of_date
              order by p.effective_from desc, p.created_at desc, p.id desc
              limit 1
            ) p on true
            where b.company_id = @company_id
              and b.is_active = true
              and b.effective_from <= @as_of_date
            order by b.is_primary desc, b.effective_from desc, b.book_code asc, b.id asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("as_of_date", asOfDate);

        var pending = new List<(CompanyBookRecord Book, CompanyBookRemeasurementPolicy? Policy, bool HasCompanyPostedHistory, bool HasBookSpecificRevaluationHistory, bool HasClosedPeriods, bool HasIssuedReports, bool HasFiledTax)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var book = new CompanyBookRecord(
                reader.GetGuid(reader.GetOrdinal("book_id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("book_code")),
                reader.GetString(reader.GetOrdinal("book_name")),
                reader.GetString(reader.GetOrdinal("book_role")),
                reader.GetString(reader.GetOrdinal("accounting_standard")),
                reader.GetString(reader.GetOrdinal("book_base_currency_code")),
                reader.GetString(reader.GetOrdinal("functional_currency_code")),
                reader.IsDBNull(reader.GetOrdinal("presentation_currency_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("presentation_currency_code")),
                reader.GetBoolean(reader.GetOrdinal("is_primary")),
                reader.GetBoolean(reader.GetOrdinal("is_adjustment_only")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("book_effective_from")),
                reader.GetBoolean(reader.GetOrdinal("book_is_active")));

            CompanyBookRemeasurementPolicy? policy = null;
            if (!reader.IsDBNull(reader.GetOrdinal("policy_id")))
            {
                policy = new CompanyBookRemeasurementPolicy(
                    reader.GetGuid(reader.GetOrdinal("policy_id")),
                    CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                    reader.GetGuid(reader.GetOrdinal("book_id")),
                    reader.GetString(reader.GetOrdinal("rate_type")),
                    reader.GetString(reader.GetOrdinal("quote_basis")),
                    reader.GetString(reader.GetOrdinal("rate_use_case")),
                    reader.GetString(reader.GetOrdinal("posting_reason")),
                    reader.GetString(reader.GetOrdinal("revaluation_profile")),
                    reader.GetString(reader.GetOrdinal("fx_rounding_policy")),
                    reader.GetFieldValue<DateOnly>(reader.GetOrdinal("policy_effective_from")),
                    reader.GetBoolean(reader.GetOrdinal("policy_is_active")));
            }

            pending.Add((
                book,
                policy,
                reader.GetBoolean(reader.GetOrdinal("has_company_posted_history")),
                reader.GetBoolean(reader.GetOrdinal("has_book_revaluation_history")),
                reader.GetBoolean(reader.GetOrdinal("has_closed_periods")),
                reader.GetBoolean(reader.GetOrdinal("has_issued_reports")),
                reader.GetBoolean(reader.GetOrdinal("has_filed_tax"))));
        }

        await reader.DisposeAsync();

        var results = new List<CompanyBookGovernanceState>(pending.Count);
        foreach (var item in pending)
        {
            var governanceSignals = await LoadGovernanceSignalsAsync(
                connection,
                companyId,
                item.Book.BookId,
                asOfDate,
                cancellationToken,
                hasClosedPeriods: item.HasClosedPeriods,
                hasIssuedReports: item.HasIssuedReports,
                hasFiledTax: item.HasFiledTax);

            results.Add(new CompanyBookGovernanceState(
                item.Book,
                item.Policy,
                item.HasCompanyPostedHistory,
                item.HasBookSpecificRevaluationHistory,
                governanceSignals));
        }

        return results;
    }

    public Task<CompanyBookGovernanceSignalSummary> GetGovernanceSignalsAsync(
        CompanyId companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken) =>
        GetGovernanceSignalsCoreAsync(companyId, bookId, asOfDate, cancellationToken);

    public async Task<CompanyBookGovernanceSignalRecord> CreateGovernanceSignalAsync(
        CompanyId companyId,
        Guid bookId,
        string signalType,
        DateOnly signalDate,
        string? referenceLabel,
        string? notes,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var signalId = Guid.NewGuid();
        command.CommandText =
            """
            insert into company_book_governance_signals (
              id,
              company_id,
              company_book_id,
              signal_type,
              signal_date,
              reference_label,
              notes,
              created_by_user_id
            )
            values (
              @id,
              @company_id,
              @company_book_id,
              @signal_type,
              @signal_date,
              @reference_label,
              @notes,
              @created_by_user_id
            );
            """;
        command.Parameters.AddWithValue("id", signalId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("company_book_id", bookId);
        command.Parameters.AddWithValue("signal_type", signalType);
        command.Parameters.AddWithValue("signal_date", signalDate);
        command.Parameters.AddWithValue("reference_label", referenceLabel is null ? DBNull.Value : referenceLabel);
        command.Parameters.AddWithValue("notes", notes is null ? DBNull.Value : notes);
        command.Parameters.AddWithValue("created_by_user_id", userId);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException(
                $"Governance signal '{signalType}' on {signalDate:yyyy-MM-dd} is already registered for this book.");
        }

        return (await LoadGovernanceSignalsAsync(connection, companyId, bookId, signalDate, cancellationToken))
            .Signals
            .First(signal => signal.SignalId == signalId);
    }

    public async Task<CompanyBookGovernedChangeRequestDraft> CreateGovernedChangeRequestDraftAsync(
        CompanyBookGovernedChangePreview preview,
        DateOnly asOfDate,
        DateOnly effectiveFrom,
        UserId userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var requestId = Guid.NewGuid();

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into company_book_governed_change_requests (
              id,
              company_id,
              company_book_id,
              status,
              requested_action,
              evaluation_basis,
              as_of_date,
              effective_from,
              has_company_posted_history,
              has_book_specific_revaluation_history,
              current_book_code,
              current_book_name,
              current_book_role,
              current_is_primary,
              current_is_adjustment_only,
              current_accounting_standard,
              current_book_base_currency_code,
              current_functional_currency_code,
              current_presentation_currency_code,
              current_book_effective_from,
              current_rate_type,
              current_quote_basis,
              current_rate_use_case,
              current_posting_reason,
              current_revaluation_profile,
              current_fx_rounding_policy,
              current_policy_effective_from,
              proposed_is_primary,
              proposed_accounting_standard,
              proposed_book_base_currency_code,
              proposed_functional_currency_code,
              proposed_presentation_currency_code,
              proposed_rate_type,
              proposed_quote_basis,
              proposed_rate_use_case,
              proposed_posting_reason,
              proposed_revaluation_profile,
              proposed_fx_rounding_policy,
              changed_fields,
              change_categories,
              reason,
              created_by_user_id,
              submitted_by_user_id,
              submitted_at,
              cancelled_by_user_id,
              cancelled_at,
              applied_at
            )
            values (
              @id,
              @company_id,
              @company_book_id,
              'draft',
              @requested_action,
              @evaluation_basis,
              @as_of_date,
              @effective_from,
              @has_company_posted_history,
              @has_book_specific_revaluation_history,
              @current_book_code,
              @current_book_name,
              @current_book_role,
              @current_is_primary,
              @current_is_adjustment_only,
              @current_accounting_standard,
              @current_book_base_currency_code,
              @current_functional_currency_code,
              @current_presentation_currency_code,
              @current_book_effective_from,
              @current_rate_type,
              @current_quote_basis,
              @current_rate_use_case,
              @current_posting_reason,
              @current_revaluation_profile,
              @current_fx_rounding_policy,
              @current_policy_effective_from,
              @proposed_is_primary,
              @proposed_accounting_standard,
              @proposed_book_base_currency_code,
              @proposed_functional_currency_code,
              @proposed_presentation_currency_code,
              @proposed_rate_type,
              @proposed_quote_basis,
              @proposed_rate_use_case,
              @proposed_posting_reason,
              @proposed_revaluation_profile,
              @proposed_fx_rounding_policy,
              @changed_fields,
              @change_categories,
              @reason,
              @created_by_user_id,
              null,
              null,
              null,
              null,
              null
            );
            """;
        command.Parameters.AddWithValue("id", requestId);
        command.Parameters.AddWithValue("company_id", preview.Book.CompanyId);
        command.Parameters.AddWithValue("company_book_id", preview.Book.BookId);
        command.Parameters.AddWithValue("requested_action", preview.ChangeImpact.RecommendedPath);
        command.Parameters.AddWithValue("evaluation_basis", preview.ChangeImpact.EvaluationBasis);
        command.Parameters.AddWithValue("as_of_date", asOfDate);
        command.Parameters.AddWithValue("effective_from", effectiveFrom);
        command.Parameters.AddWithValue("has_company_posted_history", !preview.ChangeImpact.DirectUpdateAllowed);
        command.Parameters.AddWithValue(
            "has_book_specific_revaluation_history",
            string.Equals(
                preview.ChangeImpact.RecommendedPath,
                "new_secondary_or_adjustment_book",
                StringComparison.OrdinalIgnoreCase));
        command.Parameters.AddWithValue("current_book_code", preview.Book.BookCode);
        command.Parameters.AddWithValue("current_book_name", preview.Book.BookName);
        command.Parameters.AddWithValue("current_book_role", preview.Book.BookRole);
        command.Parameters.AddWithValue("current_is_primary", preview.Book.IsPrimary);
        command.Parameters.AddWithValue("current_is_adjustment_only", preview.Book.IsAdjustmentOnly);
        command.Parameters.AddWithValue("current_accounting_standard", preview.Book.AccountingStandard);
        command.Parameters.AddWithValue("current_book_base_currency_code", preview.Book.BookBaseCurrencyCode);
        command.Parameters.AddWithValue("current_functional_currency_code", preview.Book.FunctionalCurrencyCode);
        command.Parameters.AddWithValue(
            "current_presentation_currency_code",
            preview.Book.PresentationCurrencyCode is null ? DBNull.Value : preview.Book.PresentationCurrencyCode);
        command.Parameters.AddWithValue("current_book_effective_from", preview.Book.EffectiveFrom);
        command.Parameters.AddWithValue(
            "current_rate_type",
            preview.CurrentRemeasurementPolicy?.RateType is null ? DBNull.Value : preview.CurrentRemeasurementPolicy.RateType);
        command.Parameters.AddWithValue(
            "current_quote_basis",
            preview.CurrentRemeasurementPolicy?.QuoteBasis is null ? DBNull.Value : preview.CurrentRemeasurementPolicy.QuoteBasis);
        command.Parameters.AddWithValue(
            "current_rate_use_case",
            preview.CurrentRemeasurementPolicy?.RateUseCase is null ? DBNull.Value : preview.CurrentRemeasurementPolicy.RateUseCase);
        command.Parameters.AddWithValue(
            "current_posting_reason",
            preview.CurrentRemeasurementPolicy?.PostingReason is null ? DBNull.Value : preview.CurrentRemeasurementPolicy.PostingReason);
        command.Parameters.AddWithValue(
            "current_revaluation_profile",
            preview.CurrentRemeasurementPolicy?.RevaluationProfile is null ? DBNull.Value : preview.CurrentRemeasurementPolicy.RevaluationProfile);
        command.Parameters.AddWithValue(
            "current_fx_rounding_policy",
            preview.CurrentRemeasurementPolicy?.FxRoundingPolicy is null ? DBNull.Value : preview.CurrentRemeasurementPolicy.FxRoundingPolicy);
        command.Parameters.AddWithValue(
            "current_policy_effective_from",
            preview.CurrentRemeasurementPolicy is null ? DBNull.Value : preview.CurrentRemeasurementPolicy.EffectiveFrom);
        command.Parameters.AddWithValue(
            "proposed_is_primary",
            preview.ProposedChanges.IsPrimary.HasValue ? preview.ProposedChanges.IsPrimary.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "proposed_accounting_standard",
            preview.ProposedChanges.AccountingStandard is null ? DBNull.Value : preview.ProposedChanges.AccountingStandard);
        command.Parameters.AddWithValue(
            "proposed_book_base_currency_code",
            preview.ProposedChanges.BookBaseCurrencyCode is null ? DBNull.Value : preview.ProposedChanges.BookBaseCurrencyCode);
        command.Parameters.AddWithValue(
            "proposed_functional_currency_code",
            preview.ProposedChanges.FunctionalCurrencyCode is null ? DBNull.Value : preview.ProposedChanges.FunctionalCurrencyCode);
        command.Parameters.AddWithValue(
            "proposed_presentation_currency_code",
            preview.ProposedChanges.PresentationCurrencyCode is null ? DBNull.Value : preview.ProposedChanges.PresentationCurrencyCode);
        command.Parameters.AddWithValue(
            "proposed_rate_type",
            preview.ProposedChanges.RateType is null ? DBNull.Value : preview.ProposedChanges.RateType);
        command.Parameters.AddWithValue(
            "proposed_quote_basis",
            preview.ProposedChanges.QuoteBasis is null ? DBNull.Value : preview.ProposedChanges.QuoteBasis);
        command.Parameters.AddWithValue(
            "proposed_rate_use_case",
            preview.ProposedChanges.RateUseCase is null ? DBNull.Value : preview.ProposedChanges.RateUseCase);
        command.Parameters.AddWithValue(
            "proposed_posting_reason",
            preview.ProposedChanges.PostingReason is null ? DBNull.Value : preview.ProposedChanges.PostingReason);
        command.Parameters.AddWithValue(
            "proposed_revaluation_profile",
            preview.ProposedChanges.RevaluationProfile is null ? DBNull.Value : preview.ProposedChanges.RevaluationProfile);
        command.Parameters.AddWithValue(
            "proposed_fx_rounding_policy",
            preview.ProposedChanges.FxRoundingPolicy is null ? DBNull.Value : preview.ProposedChanges.FxRoundingPolicy);
        command.Parameters.AddWithValue("changed_fields", preview.ChangeImpact.ChangedFields.ToArray());
        command.Parameters.AddWithValue("change_categories", preview.ChangeImpact.ChangeCategories.ToArray());
        command.Parameters.AddWithValue("reason", preview.ChangeImpact.Reason);
        command.Parameters.AddWithValue("created_by_user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await GetGovernedChangeRequestDraftAsync(connection, preview.Book.CompanyId, requestId, cancellationToken))!;
    }

    public async Task<CompanyBookGovernedChangeRequestDraft?> GetGovernedChangeRequestDraftAsync(
        CompanyId companyId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await GetGovernedChangeRequestDraftAsync(connection, companyId, requestId, cancellationToken);
    }

    public async Task<IReadOnlyList<CompanyBookGovernedChangeRequestDraft>> ListGovernedChangeRequestDraftsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              company_id,
              company_book_id,
              status,
              requested_action,
              evaluation_basis,
              as_of_date,
              effective_from,
              has_company_posted_history,
              has_book_specific_revaluation_history,
              current_book_code,
              current_book_name,
              current_book_role,
              current_is_primary,
              current_is_adjustment_only,
              current_accounting_standard,
              current_book_base_currency_code,
              current_functional_currency_code,
              current_presentation_currency_code,
              current_book_effective_from,
              current_rate_type,
              current_quote_basis,
              current_rate_use_case,
              current_posting_reason,
              current_revaluation_profile,
              current_fx_rounding_policy,
              current_policy_effective_from,
              proposed_is_primary,
              proposed_accounting_standard,
              proposed_book_base_currency_code,
              proposed_functional_currency_code,
              proposed_presentation_currency_code,
              proposed_rate_type,
              proposed_quote_basis,
              proposed_rate_use_case,
              proposed_posting_reason,
              proposed_revaluation_profile,
              proposed_fx_rounding_policy,
              changed_fields,
              change_categories,
              reason,
              created_by_user_id,
              created_at,
              submitted_by_user_id,
              submitted_at,
              cancelled_by_user_id,
              cancelled_at,
              applied_at
            from company_book_governed_change_requests
            where company_id = @company_id
            order by created_at desc, id desc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var results = new List<CompanyBookGovernedChangeRequestDraft>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapGovernedChangeRequestDraft(reader));
        }

        return results;
    }

    public async Task<CompanyBookGovernedChangeRequestDraft> SubmitGovernedChangeRequestDraftAsync(
        CompanyId companyId,
        Guid requestId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update company_book_governed_change_requests
            set status = 'submitted',
                submitted_by_user_id = @user_id,
                submitted_at = now(),
                updated_at = now()
            where company_id = @company_id
              and id = @id
              and status = 'draft';
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", requestId);
        command.Parameters.AddWithValue("user_id", userId.Value);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Governed change request {requestId:D} could not be submitted from its current status.");
        }

        return (await GetGovernedChangeRequestDraftAsync(connection, companyId, requestId, cancellationToken))!;
    }

    public async Task<CompanyBookGovernedChangeRequestDraft> CancelGovernedChangeRequestDraftAsync(
        CompanyId companyId,
        Guid requestId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update company_book_governed_change_requests
            set status = 'cancelled',
                cancelled_by_user_id = @user_id,
                cancelled_at = now(),
                updated_at = now()
            where company_id = @company_id
              and id = @id
              and status in ('draft', 'submitted');
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", requestId);
        command.Parameters.AddWithValue("user_id", userId.Value);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Governed change request {requestId:D} could not be cancelled from its current status.");
        }

        return (await GetGovernedChangeRequestDraftAsync(connection, companyId, requestId, cancellationToken))!;
    }

    public async Task<CompanyBookPolicyGovernanceResult?> TryGetDefaultRemeasurementPolicyAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await TryLoadPolicyAsync(
            connection,
            transaction: null,
            companyId,
            bookId: null,
            requirePrimaryBook: true,
            asOfDate,
            cancellationToken);
    }

    public async Task<CompanyBookPolicyGovernanceResult?> TryGetRemeasurementPolicyAsync(
        CompanyId companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await TryLoadPolicyAsync(
            connection,
            transaction: null,
            companyId,
            bookId,
            requirePrimaryBook: false,
            asOfDate,
            cancellationToken);
    }

    public async Task<CompanyBookPolicyGovernanceResult> EnsureDefaultPrimaryBookPolicyAsync(
        CompanyId companyId,
        UserId userId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = await TryLoadPolicyAsync(
            connection,
            transaction,
            companyId,
            bookId: null,
            requirePrimaryBook: true,
            asOfDate,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing with { WasProvisioned = false };
        }

        var companyRow = await GetCompanyRowAsync(connection, transaction, companyId, cancellationToken);
        var bookId = await EnsurePrimaryBookAsync(
            connection,
            transaction,
            companyId,
            companyRow.BaseCurrencyCode,
            userId,
            asOfDate,
            cancellationToken);
        await EnsureRemeasurementPolicyAsync(
            connection,
            transaction,
            companyId,
            bookId,
            userId,
            asOfDate,
            cancellationToken);

        var provisioned = await TryLoadPolicyAsync(
            connection,
            transaction,
            companyId,
            bookId,
            requirePrimaryBook: false,
            asOfDate,
            cancellationToken);
        if (provisioned is null)
        {
            throw new InvalidOperationException(
                $"Company {companyId:D} default primary book remeasurement policy could not be reloaded after provisioning.");
        }

        await transaction.CommitAsync(cancellationToken);
        return provisioned with { WasProvisioned = true };
    }

    private static async Task<Guid> EnsurePrimaryBookAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string baseCurrencyCode,
        UserId userId,
        DateOnly effectiveFrom,
        CancellationToken cancellationToken)
    {
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText =
                """
                select id
                from company_books
                where company_id = @company_id
                  and is_active = true
                  and is_primary = true
                order by effective_from desc, created_at desc, id desc
                limit 1
                for update;
                """;
            existingCommand.Parameters.AddWithValue("company_id", companyId.Value);

            var existing = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid existingBookId)
            {
                return existingBookId;
            }
        }

        var bookId = Guid.NewGuid();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText =
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
            values (
              @id,
              @company_id,
              @book_code,
              @book_name,
              @book_role,
              @accounting_standard,
              @book_base_currency_code,
              @functional_currency_code,
              @presentation_currency_code,
              true,
              false,
              @effective_from,
              true,
              @created_by_user_id,
              now(),
              now()
            );
            """;
        insertCommand.Parameters.AddWithValue("id", bookId);
        insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
        insertCommand.Parameters.AddWithValue("book_code", DefaultBookCode);
        insertCommand.Parameters.AddWithValue("book_name", DefaultBookName);
        insertCommand.Parameters.AddWithValue("book_role", DefaultBookRole);
        insertCommand.Parameters.AddWithValue("accounting_standard", DefaultAccountingStandard);
        insertCommand.Parameters.AddWithValue("book_base_currency_code", baseCurrencyCode);
        insertCommand.Parameters.AddWithValue("functional_currency_code", baseCurrencyCode);
        insertCommand.Parameters.AddWithValue("presentation_currency_code", DBNull.Value);
        insertCommand.Parameters.AddWithValue("effective_from", effectiveFrom);
        insertCommand.Parameters.AddWithValue("created_by_user_id", userId);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        return bookId;
    }

    private static async Task EnsureRemeasurementPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid bookId,
        UserId userId,
        DateOnly effectiveFrom,
        CancellationToken cancellationToken)
    {
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText =
                """
                select id
                from company_book_remeasurement_policies
                where company_id = @company_id
                  and company_book_id = @company_book_id
                  and is_active = true
                order by effective_from desc, created_at desc, id desc
                limit 1
                for update;
                """;
            existingCommand.Parameters.AddWithValue("company_id", companyId.Value);
            existingCommand.Parameters.AddWithValue("company_book_id", bookId);

            var existing = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid)
            {
                return;
            }
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText =
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
            values (
              @id,
              @company_id,
              @company_book_id,
              @rate_type,
              @quote_basis,
              @rate_use_case,
              @posting_reason,
              @revaluation_profile,
              @fx_rounding_policy,
              @effective_from,
              true,
              @created_by_user_id,
              now(),
              now()
            );
            """;
        insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
        insertCommand.Parameters.AddWithValue("company_book_id", bookId);
        insertCommand.Parameters.AddWithValue("rate_type", DefaultRateType);
        insertCommand.Parameters.AddWithValue("quote_basis", DefaultQuoteBasis);
        insertCommand.Parameters.AddWithValue("rate_use_case", DefaultRateUseCase);
        insertCommand.Parameters.AddWithValue("posting_reason", DefaultPostingReason);
        insertCommand.Parameters.AddWithValue("revaluation_profile", DefaultRevaluationProfile);
        insertCommand.Parameters.AddWithValue("fx_rounding_policy", DefaultFxRoundingPolicy);
        insertCommand.Parameters.AddWithValue("effective_from", effectiveFrom);
        insertCommand.Parameters.AddWithValue("created_by_user_id", userId);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(string BaseCurrencyCode, string Status)> GetCompanyRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select base_currency_code, status
            from companies
            where id = @company_id
            limit 1
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Company {companyId:D} was not found.");
        }

        var status = reader.GetString(reader.GetOrdinal("status"));
        if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Company {companyId:D} is not active.");
        }

        return (
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            status);
    }

    private static async Task<CompanyBookPolicyGovernanceResult?> TryLoadPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid? bookId,
        bool requirePrimaryBook,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              b.id as book_id,
              b.company_id,
              b.book_code,
              b.book_name,
              b.book_role,
              b.accounting_standard,
              b.book_base_currency_code,
              b.functional_currency_code,
              b.presentation_currency_code,
              b.is_primary,
              b.is_adjustment_only,
              b.effective_from as book_effective_from,
              b.is_active as book_is_active,
              p.id as policy_id,
              p.rate_type,
              p.quote_basis,
              p.rate_use_case,
              p.posting_reason,
              p.revaluation_profile,
              p.fx_rounding_policy,
              p.effective_from as policy_effective_from,
              p.is_active as policy_is_active
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
              and (@require_primary is false or b.is_primary = true)
            order by
              p.effective_from desc,
              b.effective_from desc,
              p.created_at desc,
              b.created_at desc,
              p.id desc,
              b.id desc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("book_id", bookId.HasValue ? bookId.Value : DBNull.Value);
        command.Parameters.AddWithValue("require_primary", requirePrimaryBook);
        command.Parameters.AddWithValue("as_of_date", asOfDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var book = new CompanyBookRecord(
            reader.GetGuid(reader.GetOrdinal("book_id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetString(reader.GetOrdinal("book_code")),
            reader.GetString(reader.GetOrdinal("book_name")),
            reader.GetString(reader.GetOrdinal("book_role")),
            reader.GetString(reader.GetOrdinal("accounting_standard")),
            reader.GetString(reader.GetOrdinal("book_base_currency_code")),
            reader.GetString(reader.GetOrdinal("functional_currency_code")),
            reader.IsDBNull(reader.GetOrdinal("presentation_currency_code"))
                ? null
                : reader.GetString(reader.GetOrdinal("presentation_currency_code")),
            reader.GetBoolean(reader.GetOrdinal("is_primary")),
            reader.GetBoolean(reader.GetOrdinal("is_adjustment_only")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("book_effective_from")),
            reader.GetBoolean(reader.GetOrdinal("book_is_active")));

        var policy = new CompanyBookRemeasurementPolicy(
            reader.GetGuid(reader.GetOrdinal("policy_id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetGuid(reader.GetOrdinal("book_id")),
            reader.GetString(reader.GetOrdinal("rate_type")),
            reader.GetString(reader.GetOrdinal("quote_basis")),
            reader.GetString(reader.GetOrdinal("rate_use_case")),
            reader.GetString(reader.GetOrdinal("posting_reason")),
            reader.GetString(reader.GetOrdinal("revaluation_profile")),
            reader.GetString(reader.GetOrdinal("fx_rounding_policy")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("policy_effective_from")),
            reader.GetBoolean(reader.GetOrdinal("policy_is_active")));

        return new CompanyBookPolicyGovernanceResult(book, policy, false);
    }

    private static async Task<CompanyBookGovernedChangeRequestDraft?> GetGovernedChangeRequestDraftAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              company_id,
              company_book_id,
              status,
              requested_action,
              evaluation_basis,
              as_of_date,
              effective_from,
              has_company_posted_history,
              has_book_specific_revaluation_history,
              current_book_code,
              current_book_name,
              current_book_role,
              current_is_primary,
              current_is_adjustment_only,
              current_accounting_standard,
              current_book_base_currency_code,
              current_functional_currency_code,
              current_presentation_currency_code,
              current_book_effective_from,
              current_rate_type,
              current_quote_basis,
              current_rate_use_case,
              current_posting_reason,
              current_revaluation_profile,
              current_fx_rounding_policy,
              current_policy_effective_from,
              proposed_is_primary,
              proposed_accounting_standard,
              proposed_book_base_currency_code,
              proposed_functional_currency_code,
              proposed_presentation_currency_code,
              proposed_rate_type,
              proposed_quote_basis,
              proposed_rate_use_case,
              proposed_posting_reason,
              proposed_revaluation_profile,
              proposed_fx_rounding_policy,
              changed_fields,
              change_categories,
              reason,
              created_by_user_id,
              created_at,
              submitted_by_user_id,
              submitted_at,
              cancelled_by_user_id,
              cancelled_at,
              applied_at
            from company_book_governed_change_requests
            where id = @id
              and company_id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("id", requestId);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapGovernedChangeRequestDraft(reader);
    }

    private static CompanyBookGovernedChangeRequestDraft MapGovernedChangeRequestDraft(NpgsqlDataReader reader)
    {
        var bookId = reader.GetGuid(reader.GetOrdinal("company_book_id"));
        var companyId = CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id")));
        var book = new CompanyBookRecord(
            bookId,
            companyId,
            reader.GetString(reader.GetOrdinal("current_book_code")),
            reader.GetString(reader.GetOrdinal("current_book_name")),
            reader.GetString(reader.GetOrdinal("current_book_role")),
            reader.GetString(reader.GetOrdinal("current_accounting_standard")),
            reader.GetString(reader.GetOrdinal("current_book_base_currency_code")),
            reader.GetString(reader.GetOrdinal("current_functional_currency_code")),
            reader.IsDBNull(reader.GetOrdinal("current_presentation_currency_code"))
                ? null
                : reader.GetString(reader.GetOrdinal("current_presentation_currency_code")),
            reader.GetBoolean(reader.GetOrdinal("current_is_primary")),
            reader.GetBoolean(reader.GetOrdinal("current_is_adjustment_only")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("current_book_effective_from")),
            true);

        CompanyBookRemeasurementPolicy? policy = null;
        if (!reader.IsDBNull(reader.GetOrdinal("current_policy_effective_from")))
        {
            policy = new CompanyBookRemeasurementPolicy(
                Guid.Empty,
                companyId,
                bookId,
                reader.IsDBNull(reader.GetOrdinal("current_rate_type"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("current_rate_type")),
                reader.IsDBNull(reader.GetOrdinal("current_quote_basis"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("current_quote_basis")),
                reader.IsDBNull(reader.GetOrdinal("current_rate_use_case"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("current_rate_use_case")),
                reader.IsDBNull(reader.GetOrdinal("current_posting_reason"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("current_posting_reason")),
                reader.IsDBNull(reader.GetOrdinal("current_revaluation_profile"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("current_revaluation_profile")),
                reader.IsDBNull(reader.GetOrdinal("current_fx_rounding_policy"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("current_fx_rounding_policy")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("current_policy_effective_from")),
                true);
        }

        var proposedChanges = new CompanyBookProposedChangeSet(
            reader.IsDBNull(reader.GetOrdinal("proposed_is_primary"))
                ? null
                : reader.GetBoolean(reader.GetOrdinal("proposed_is_primary")),
            reader.IsDBNull(reader.GetOrdinal("proposed_accounting_standard"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_accounting_standard")),
            reader.IsDBNull(reader.GetOrdinal("proposed_book_base_currency_code"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_book_base_currency_code")),
            reader.IsDBNull(reader.GetOrdinal("proposed_functional_currency_code"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_functional_currency_code")),
            reader.IsDBNull(reader.GetOrdinal("proposed_presentation_currency_code"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_presentation_currency_code")),
            reader.IsDBNull(reader.GetOrdinal("proposed_rate_type"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_rate_type")),
            reader.IsDBNull(reader.GetOrdinal("proposed_quote_basis"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_quote_basis")),
            reader.IsDBNull(reader.GetOrdinal("proposed_rate_use_case"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_rate_use_case")),
            reader.IsDBNull(reader.GetOrdinal("proposed_posting_reason"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_posting_reason")),
            reader.IsDBNull(reader.GetOrdinal("proposed_revaluation_profile"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_revaluation_profile")),
            reader.IsDBNull(reader.GetOrdinal("proposed_fx_rounding_policy"))
                ? null
                : reader.GetString(reader.GetOrdinal("proposed_fx_rounding_policy")));

        var requestedAction = reader.GetString(reader.GetOrdinal("requested_action"));
        var directUpdateAllowed = string.Equals(requestedAction, "direct_update_in_place", StringComparison.OrdinalIgnoreCase);
        var changeImpact = new CompanyBookChangeImpact(
            HasAnyChange: true,
            ChangedFields: reader.GetFieldValue<string[]>(reader.GetOrdinal("changed_fields")),
            ChangeCategories: reader.GetFieldValue<string[]>(reader.GetOrdinal("change_categories")),
            DirectUpdateAllowed: directUpdateAllowed,
            GovernedMigrationRequired: !directUpdateAllowed,
            RecommendedPath: requestedAction,
            EvaluationBasis: reader.GetString(reader.GetOrdinal("evaluation_basis")),
            Reason: reader.GetString(reader.GetOrdinal("reason")));

        return new CompanyBookGovernedChangeRequestDraft(
            reader.GetGuid(reader.GetOrdinal("id")),
            companyId,
            bookId,
            reader.GetString(reader.GetOrdinal("status")),
            requestedAction,
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("as_of_date")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("effective_from")),
            UserId.Parse(reader.GetString(reader.GetOrdinal("created_by_user_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.IsDBNull(reader.GetOrdinal("submitted_by_user_id"))
                ? null
                : UserId.Parse(reader.GetString(reader.GetOrdinal("submitted_by_user_id"))),
            reader.IsDBNull(reader.GetOrdinal("submitted_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("submitted_at")),
            reader.IsDBNull(reader.GetOrdinal("cancelled_by_user_id"))
                ? null
                : UserId.Parse(reader.GetString(reader.GetOrdinal("cancelled_by_user_id"))),
            reader.IsDBNull(reader.GetOrdinal("cancelled_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("cancelled_at")),
            reader.IsDBNull(reader.GetOrdinal("applied_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("applied_at")),
            new CompanyBookGovernedChangePreview(
                book,
                policy,
                proposedChanges,
                changeImpact));
    }

    private async Task<CompanyBookGovernanceSignalSummary> GetGovernanceSignalsCoreAsync(
        CompanyId companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await LoadGovernanceSignalsAsync(
            connection,
            companyId,
            bookId,
            asOfDate,
            cancellationToken);
    }

    private static async Task<CompanyBookGovernanceSignalSummary> LoadGovernanceSignalsAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken,
        bool? hasClosedPeriods = null,
        bool? hasIssuedReports = null,
        bool? hasFiledTax = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              company_id,
              company_book_id,
              signal_type,
              signal_date,
              reference_label,
              notes,
              created_by_user_id,
              created_at
            from company_book_governance_signals
            where company_id = @company_id
              and company_book_id = @company_book_id
              and signal_date <= @as_of_date
            order by signal_date desc, created_at desc, id desc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("company_book_id", bookId);
        command.Parameters.AddWithValue("as_of_date", asOfDate);

        var signals = new List<CompanyBookGovernanceSignalRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            signals.Add(new CompanyBookGovernanceSignalRecord(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetGuid(reader.GetOrdinal("company_book_id")),
                reader.GetString(reader.GetOrdinal("signal_type")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("signal_date")),
                reader.IsDBNull(reader.GetOrdinal("reference_label"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("reference_label")),
                reader.IsDBNull(reader.GetOrdinal("notes"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("notes")),
                reader.IsDBNull(reader.GetOrdinal("created_by_user_id"))
                    ? null
                    : UserId.Parse(reader.GetString(reader.GetOrdinal("created_by_user_id"))),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"))));
        }

        return new CompanyBookGovernanceSignalSummary(
            hasClosedPeriods ?? signals.Any(signal => string.Equals(signal.SignalType, "closed_period", StringComparison.OrdinalIgnoreCase)),
            hasIssuedReports ?? signals.Any(signal => string.Equals(signal.SignalType, "reported_statement", StringComparison.OrdinalIgnoreCase)),
            hasFiledTax ?? signals.Any(signal => string.Equals(signal.SignalType, "filed_tax", StringComparison.OrdinalIgnoreCase)),
            signals);
    }
}
