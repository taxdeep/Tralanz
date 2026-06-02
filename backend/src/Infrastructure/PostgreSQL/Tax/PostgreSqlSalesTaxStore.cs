using Citus.Accounting.Application.Abstractions;
using Npgsql;
using SharedKernel.Identity;

namespace Infrastructure.PostgreSQL.Tax;

public sealed class PostgreSqlSalesTaxStore(PostgreSqlConnectionFactory connections) : ISalesTaxStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sales_tax_jurisdictions (
                id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id     TEXT NOT NULL,
                country_code   TEXT NOT NULL,
                region_code    TEXT NULL,
                locality_name  TEXT NULL,
                level          TEXT NOT NULL,
                parent_id      UUID NULL,
                is_active      BOOLEAN NOT NULL DEFAULT TRUE,
                created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS sales_tax_authorities (
                id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id       TEXT NOT NULL,
                jurisdiction_id  UUID NULL,
                code             TEXT NOT NULL,
                name             TEXT NOT NULL,
                authority_type   TEXT NOT NULL,
                is_active        BOOLEAN NOT NULL DEFAULT TRUE,
                created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(company_id, code)
            );

            CREATE TABLE IF NOT EXISTS sales_tax_registrations (
                id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id            TEXT NOT NULL,
                authority_id          UUID NOT NULL,
                registration_number   TEXT NOT NULL,
                filing_frequency      TEXT NOT NULL DEFAULT 'quarterly',
                effective_from        DATE NULL,
                effective_to          DATE NULL,
                is_active             BOOLEAN NOT NULL DEFAULT TRUE,
                created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS sales_tax_components (
                id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id            TEXT NOT NULL,
                authority_id          UUID NULL,
                code                  TEXT NOT NULL,
                name                  TEXT NOT NULL,
                tax_type              TEXT NOT NULL DEFAULT 'sales_tax',
                applies_to            TEXT NOT NULL DEFAULT 'both',
                treatment             TEXT NOT NULL DEFAULT 'taxable',
                recoverability        TEXT NOT NULL DEFAULT 'not_applicable',
                registration_number   TEXT NULL,
                is_active             BOOLEAN NOT NULL DEFAULT TRUE,
                created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(company_id, code)
            );

            ALTER TABLE sales_tax_components
                ADD COLUMN IF NOT EXISTS applies_to TEXT NOT NULL DEFAULT 'both';

            ALTER TABLE sales_tax_components
                ADD COLUMN IF NOT EXISTS registration_number TEXT NULL;

            CREATE TABLE IF NOT EXISTS sales_tax_component_rates (
                id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id            TEXT NOT NULL,
                tax_component_id      UUID NOT NULL,
                rate_percent          NUMERIC(9,6) NOT NULL,
                effective_from        DATE NOT NULL,
                effective_to          DATE NULL,
                source                TEXT NOT NULL DEFAULT 'manual',
                source_ref            TEXT NULL,
                created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS sales_tax_code_components (
                id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id            TEXT NOT NULL,
                tax_code_id           UUID NOT NULL,
                tax_component_id      UUID NOT NULL,
                sequence              INTEGER NOT NULL DEFAULT 1,
                applies_to            TEXT NOT NULL DEFAULT 'both',
                compound_mode         TEXT NOT NULL DEFAULT 'none',
                recoverability_override TEXT NULL,
                recoverable_percent   NUMERIC(9,6) NOT NULL DEFAULT 100,
                registration_number   TEXT NULL,
                created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(company_id, tax_code_id, tax_component_id)
            );

            ALTER TABLE sales_tax_code_components
                ADD COLUMN IF NOT EXISTS registration_number TEXT NULL;

            CREATE TABLE IF NOT EXISTS sales_tax_account_mappings (
                id                         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id                 TEXT NOT NULL,
                tax_component_id           UUID NOT NULL,
                applies_to                 TEXT NOT NULL DEFAULT 'both',
                payable_account_id         UUID NULL,
                recoverable_account_id     UUID NULL,
                nonrecoverable_account_id  UUID NULL,
                clearing_account_id        UUID NULL,
                created_at                 TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at                 TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS sales_tax_reporting_box_mappings (
                id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id            TEXT NOT NULL,
                tax_component_id      UUID NULL,
                registration_id       UUID NULL,
                report_type           TEXT NOT NULL,
                box_code              TEXT NOT NULL,
                sign                  INTEGER NOT NULL DEFAULT 1,
                created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS sales_tax_transaction_snapshots (
                id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id               TEXT NOT NULL,
                document_type            TEXT NOT NULL,
                document_id              UUID NOT NULL,
                document_line_id         UUID NULL,
                document_date            DATE NOT NULL,
                tax_code_id              UUID NULL,
                tax_component_id         UUID NULL,
                jurisdiction_id          UUID NULL,
                registration_id          UUID NULL,
                tax_code                 TEXT NOT NULL,
                tax_component_code       TEXT NOT NULL,
                jurisdiction_code        TEXT NOT NULL DEFAULT '',
                registration_number      TEXT NOT NULL DEFAULT '',
                taxable_amount           NUMERIC(18,6) NOT NULL,
                tax_amount               NUMERIC(18,6) NOT NULL,
                recoverable_amount       NUMERIC(18,6) NOT NULL DEFAULT 0,
                nonrecoverable_amount    NUMERIC(18,6) NOT NULL DEFAULT 0,
                rate_percent             NUMERIC(9,6) NOT NULL,
                treatment                TEXT NOT NULL,
                recoverability           TEXT NOT NULL,
                reporting_category       TEXT NOT NULL,
                calculation_version      TEXT NOT NULL DEFAULT 'sales-tax-v1',
                snapshot_json            JSONB NOT NULL DEFAULT '{}'::jsonb,
                posted_at                TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_sales_tax_snapshots_company_date
                ON sales_tax_transaction_snapshots(company_id, document_date);
            CREATE INDEX IF NOT EXISTS idx_sales_tax_snapshots_company_component
                ON sales_tax_transaction_snapshots(company_id, tax_component_id);
""";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SalesTaxComponentRecord>> ListTaxRulesAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var rows = new List<SalesTaxComponentRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stc.id,
                   stc.company_id::text,
                   stc.authority_id,
                   stc.code,
                   stc.name,
                   stc.tax_type,
                   stc.applies_to,
                   stc.treatment,
                   stc.recoverability,
                   COALESCE(rate.rate_percent, 0) AS current_rate_percent,
                   mapping.payable_account_id,
                   mapping.recoverable_account_id,
                   stc.registration_number,
                   rate.effective_from,
                   rate.effective_to,
                   stc.is_active
              FROM sales_tax_components stc
         LEFT JOIN LATERAL (
                SELECT r.rate_percent, r.effective_from, r.effective_to
                  FROM sales_tax_component_rates r
                 WHERE r.company_id = stc.company_id
                   AND r.tax_component_id = stc.id
                   AND r.effective_from <= CURRENT_DATE
                   AND (r.effective_to IS NULL OR r.effective_to >= CURRENT_DATE)
              ORDER BY r.effective_from DESC
                 LIMIT 1
         ) rate ON TRUE
         LEFT JOIN LATERAL (
                SELECT m.payable_account_id, m.recoverable_account_id
                  FROM sales_tax_account_mappings m
                 WHERE m.company_id = stc.company_id
                   AND m.tax_component_id = stc.id
                   AND m.applies_to = stc.applies_to
              ORDER BY m.updated_at DESC, m.created_at DESC
                 LIMIT 1
         ) mapping ON TRUE
             WHERE stc.company_id = @company_id
               AND (@include_inactive OR stc.is_active = TRUE)
          ORDER BY stc.is_active DESC, stc.code;
""";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("include_inactive", includeInactive);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapTaxRule(reader));
        }

        return rows;
    }

    public async Task<SalesTaxComponentRecord> CreateTaxRuleAsync(
        CompanyId companyId,
        SalesTaxRuleUpsertInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var id = await UpsertComponentAsync(
            connection,
            transaction,
            companyId,
            input.Code.Trim(),
            input.Name.Trim(),
            input.TaxType,
            input.AppliesTo,
            input.Treatment,
            input.Recoverability,
            input.RegistrationNumber,
            input.IsActive,
            now,
            cancellationToken).ConfigureAwait(false);

        await UpsertCurrentRateAsync(
            connection,
            transaction,
            companyId,
            id,
            input.RatePercent,
            cancellationToken).ConfigureAwait(false);

        await ReplaceRuleAccountMappingAsync(
            connection,
            transaction,
            companyId,
            id,
            input,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetTaxRuleOrThrowAsync(companyId, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SalesTaxComponentRecord?> UpdateTaxRuleAsync(
        CompanyId companyId,
        Guid taxRuleId,
        SalesTaxRuleUpsertInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var affected = 0;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sales_tax_components
                   SET code = @code,
                       name = @name,
                       tax_type = @tax_type,
                       applies_to = @applies_to,
                       treatment = @treatment,
                       recoverability = @recoverability,
                       registration_number = @registration_number,
                       is_active = @is_active,
                       updated_at = @now
                 WHERE company_id = @company_id
                   AND id = @id;
""";
            command.Parameters.AddWithValue("id", taxRuleId);
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("code", input.Code.Trim());
            command.Parameters.AddWithValue("name", input.Name.Trim());
            command.Parameters.AddWithValue("tax_type", input.TaxType);
            command.Parameters.AddWithValue("applies_to", input.AppliesTo);
            command.Parameters.AddWithValue("treatment", input.Treatment);
            command.Parameters.AddWithValue("recoverability", input.Recoverability);
            command.Parameters.AddWithValue("registration_number", DbValue(input.RegistrationNumber));
            command.Parameters.AddWithValue("is_active", input.IsActive);
            command.Parameters.AddWithValue("now", now);
            affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await UpsertCurrentRateAsync(
            connection,
            transaction,
            companyId,
            taxRuleId,
            input.RatePercent,
            cancellationToken).ConfigureAwait(false);

        await ReplaceRuleAccountMappingAsync(
            connection,
            transaction,
            companyId,
            taxRuleId,
            input,
            now,
            cancellationToken).ConfigureAwait(false);

        await RefreshLegacyCodeRatesForRuleAsync(
            connection,
            transaction,
            companyId,
            taxRuleId,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetTaxRuleOrThrowAsync(companyId, taxRuleId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SalesTaxCodeRecord>> ListTaxCodesAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tc.id,
                   tc.company_id::text,
                   tc.entity_number,
                   tc.code,
                   tc.name,
                   tc.rate_percent,
                   tc.applies_to,
                   tc.registration_number,
                   tc.is_active,
                   tc.created_at,
                   tc.updated_at,
                   tcc.tax_component_id,
                   COALESCE(stc.code, tc.code) AS component_code,
                   COALESCE(stc.name, tc.name) AS component_name,
                   COALESCE(stc.tax_type, 'sales_tax') AS component_tax_type,
                   COALESCE(tcc.applies_to, tc.applies_to) AS component_applies_to,
                   COALESCE(rate.rate_percent, tc.rate_percent) AS component_rate,
                   COALESCE(tcc.sequence, 1) AS component_sequence,
                   COALESCE(tcc.compound_mode, 'none') AS compound_mode,
                   COALESCE(stc.treatment, CASE WHEN tc.rate_percent = 0 THEN 'exempt' ELSE 'taxable' END) AS treatment,
                   COALESCE(tcc.recoverability_override, stc.recoverability,
                       CASE WHEN tc.applies_to IN ('purchase','both') AND tc.rate_percent > 0 THEN 'recoverable' ELSE 'not_applicable' END) AS recoverability,
                   COALESCE(tcc.recoverable_percent, 100) AS recoverable_percent,
                   COALESCE(tcc.registration_number, tc.registration_number) AS component_registration_number
              FROM tax_codes tc
         LEFT JOIN sales_tax_code_components tcc
                ON tcc.company_id = tc.company_id::text
               AND tcc.tax_code_id = tc.id
         LEFT JOIN sales_tax_components stc
                ON stc.company_id = tc.company_id::text
               AND stc.id = tcc.tax_component_id
         LEFT JOIN LATERAL (
                SELECT r.rate_percent
                  FROM sales_tax_component_rates r
                 WHERE r.company_id = tc.company_id::text
                   AND r.tax_component_id = stc.id
                   AND r.effective_from <= CURRENT_DATE
                   AND (r.effective_to IS NULL OR r.effective_to >= CURRENT_DATE)
              ORDER BY r.effective_from DESC
                 LIMIT 1
         ) rate ON TRUE
             WHERE tc.company_id::text = @company_id
               AND (@include_inactive OR tc.is_active = TRUE)
          ORDER BY tc.is_active DESC, tc.code, component_sequence, component_code;
""";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("include_inactive", includeInactive);

        var rows = new Dictionary<Guid, SalesTaxCodeBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            if (!rows.TryGetValue(id, out var builder))
            {
                builder = new SalesTaxCodeBuilder(
                    Id: id,
                    CompanyId: CompanyId.Parse(reader.GetString(1)),
                    EntityNumber: reader.GetString(2),
                    Code: reader.GetString(3),
                    Name: reader.GetString(4),
                    RatePercent: reader.GetDecimal(5),
                    AppliesTo: reader.GetString(6),
                    RegistrationNumber: reader.IsDBNull(7) ? null : reader.GetString(7),
                    IsActive: reader.GetBoolean(8),
                    CreatedAt: reader.GetFieldValue<DateTimeOffset>(9),
                    UpdatedAt: reader.GetFieldValue<DateTimeOffset>(10));
                rows.Add(id, builder);
            }

            builder.Components.Add(new SalesTaxCodeComponentRecord(
                TaxCodeId: id,
                TaxComponentId: reader.IsDBNull(11) ? null : reader.GetGuid(11),
                Code: reader.GetString(12),
                Name: reader.GetString(13),
                TaxType: reader.GetString(14),
                AppliesTo: reader.GetString(15),
                RatePercent: reader.GetDecimal(16),
                Sequence: reader.GetInt32(17),
                CompoundMode: reader.GetString(18),
                Treatment: reader.GetString(19),
                Recoverability: reader.GetString(20),
                RecoverablePercent: reader.GetDecimal(21),
                RegistrationNumber: reader.IsDBNull(22) ? null : reader.GetString(22)));
        }

        return rows.Values.Select(static row => row.ToRecord()).ToArray();
    }

    public async Task<SalesTaxCodeRecord> CreateTaxCodeAsync(
        CompanyId companyId,
        SalesTaxCodeUpsertInput input,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var entityNumber = GenerateEntityNumber();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var resolvedInput = await ResolveTaxCodeInputAsync(
            connection,
            transaction,
            companyId,
            input,
            cancellationToken).ConfigureAwait(false);
        var ratePercent = AggregateRate(resolvedInput.Components, resolvedInput.AppliesTo);
        var recoverableTaxAccountId = HasRecoverablePurchaseComponent(resolvedInput)
            ? await ResolveRecoverableTaxAccountAsync(connection, companyId, resolvedInput, cancellationToken).ConfigureAwait(false)
            : (Guid?)null;
        var payableTaxAccountId = HasSalesTaxComponent(resolvedInput)
            ? await ResolvePayableTaxAccountAsync(connection, companyId, resolvedInput, cancellationToken).ConfigureAwait(false)
            : (Guid?)null;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tax_codes (
                    id, company_id, entity_number, code, name, rate_percent,
                    applies_to, registration_number,
                    is_recoverable_on_purchase, recoverability_mode, payable_account_id, recoverable_account_id,
                    is_active, created_at, updated_at)
                VALUES (
                    @id, @company_id, @entity_number, @code, @name, @rate_percent,
                    @applies_to, @registration_number,
                    @is_recoverable_on_purchase, @recoverability_mode, @payable_account_id, @recoverable_account_id,
                    @is_active, @now, @now);
""";
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("code", input.Code.Trim());
            command.Parameters.AddWithValue("name", input.Name.Trim());
            command.Parameters.AddWithValue("rate_percent", ratePercent);
            command.Parameters.AddWithValue("applies_to", input.AppliesTo);
            command.Parameters.AddWithValue("registration_number", DbValue(input.RegistrationNumber));
            command.Parameters.AddWithValue("is_recoverable_on_purchase", recoverableTaxAccountId is not null);
            command.Parameters.AddWithValue("recoverability_mode", recoverableTaxAccountId is null ? "none" : "full");
            command.Parameters.AddWithValue("payable_account_id", (object?)payableTaxAccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("recoverable_account_id", (object?)recoverableTaxAccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("is_active", input.IsActive);
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReplaceComponentsAsync(connection, transaction, companyId, id, resolvedInput, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetTaxCodeOrThrowAsync(companyId, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SalesTaxCodeRecord?> UpdateTaxCodeAsync(
        CompanyId companyId,
        Guid taxCodeId,
        SalesTaxCodeUpsertInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var resolvedInput = await ResolveTaxCodeInputAsync(
            connection,
            transaction,
            companyId,
            input,
            cancellationToken).ConfigureAwait(false);
        var ratePercent = AggregateRate(resolvedInput.Components, resolvedInput.AppliesTo);
        var recoverableTaxAccountId = HasRecoverablePurchaseComponent(resolvedInput)
            ? await ResolveRecoverableTaxAccountAsync(connection, companyId, resolvedInput, cancellationToken).ConfigureAwait(false)
            : (Guid?)null;
        var payableTaxAccountId = HasSalesTaxComponent(resolvedInput)
            ? await ResolvePayableTaxAccountAsync(connection, companyId, resolvedInput, cancellationToken).ConfigureAwait(false)
            : (Guid?)null;

        var affected = 0;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE tax_codes
                   SET code = @code,
                       name = @name,
                       rate_percent = @rate_percent,
                       applies_to = @applies_to,
                       registration_number = @registration_number,
                       is_recoverable_on_purchase = @is_recoverable_on_purchase,
                       recoverability_mode = @recoverability_mode,
                       payable_account_id = @payable_account_id,
                       recoverable_account_id = @recoverable_account_id,
                       is_active = @is_active,
                       updated_at = @now
                 WHERE company_id = @company_id AND id = @id;
""";
            command.Parameters.AddWithValue("id", taxCodeId);
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("code", input.Code.Trim());
            command.Parameters.AddWithValue("name", input.Name.Trim());
            command.Parameters.AddWithValue("rate_percent", ratePercent);
            command.Parameters.AddWithValue("applies_to", input.AppliesTo);
            command.Parameters.AddWithValue("registration_number", DbValue(input.RegistrationNumber));
            command.Parameters.AddWithValue("is_recoverable_on_purchase", recoverableTaxAccountId is not null);
            command.Parameters.AddWithValue("recoverability_mode", recoverableTaxAccountId is null ? "none" : "full");
            command.Parameters.AddWithValue("payable_account_id", (object?)payableTaxAccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("recoverable_account_id", (object?)recoverableTaxAccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("is_active", input.IsActive);
            command.Parameters.AddWithValue("now", now);
            affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await ReplaceComponentsAsync(connection, transaction, companyId, taxCodeId, resolvedInput, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetTaxCodeOrThrowAsync(companyId, taxCodeId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SalesTaxReportSummaryRow>> GetSummaryReportAsync(
        CompanyId companyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var rows = new List<SalesTaxReportSummaryRow>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jurisdiction_code,
                   registration_number,
                   tax_component_code,
                   reporting_category,
                   SUM(taxable_amount),
                   SUM(CASE WHEN reporting_category = 'tax_collected' THEN tax_amount ELSE 0 END),
                   SUM(recoverable_amount),
                   SUM(nonrecoverable_amount),
                   SUM(CASE WHEN reporting_category = 'tax_collected' THEN tax_amount ELSE 0 END)
                     - SUM(recoverable_amount) AS net_tax
              FROM sales_tax_transaction_snapshots
             WHERE company_id = @company_id
               AND document_date >= @from
               AND document_date <= @to
          GROUP BY jurisdiction_code, registration_number, tax_component_code, reporting_category
          ORDER BY jurisdiction_code, tax_component_code, reporting_category;
""";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("from", from.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("to", to.ToDateTime(TimeOnly.MinValue));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SalesTaxReportSummaryRow(
                JurisdictionCode: reader.GetString(0),
                RegistrationNumber: reader.GetString(1),
                TaxComponentCode: reader.GetString(2),
                ReportingCategory: reader.GetString(3),
                TaxableAmount: reader.GetDecimal(4),
                TaxCollected: reader.GetDecimal(5),
                InputTaxRecoverable: reader.GetDecimal(6),
                NonRecoverableTax: reader.GetDecimal(7),
                NetTax: reader.GetDecimal(8)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<SalesTaxReportDetailRow>> GetDetailReportAsync(
        CompanyId companyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var rows = new List<SalesTaxReportDetailRow>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_date,
                   document_type,
                   document_id,
                   document_line_id,
                   tax_code,
                   tax_component_code,
                   jurisdiction_code,
                   taxable_amount,
                   rate_percent,
                   tax_amount,
                   recoverable_amount,
                   nonrecoverable_amount,
                   reporting_category
              FROM sales_tax_transaction_snapshots
             WHERE company_id = @company_id
               AND document_date >= @from
               AND document_date <= @to
          ORDER BY document_date, document_type, document_id, tax_component_code;
""";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("from", from.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("to", to.ToDateTime(TimeOnly.MinValue));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SalesTaxReportDetailRow(
                DocumentDate: DateOnly.FromDateTime(reader.GetDateTime(0)),
                DocumentType: reader.GetString(1),
                DocumentId: reader.GetGuid(2),
                DocumentLineId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                TaxCode: reader.GetString(4),
                TaxComponentCode: reader.GetString(5),
                JurisdictionCode: reader.GetString(6),
                TaxableAmount: reader.GetDecimal(7),
                RatePercent: reader.GetDecimal(8),
                TaxAmount: reader.GetDecimal(9),
                RecoverableAmount: reader.GetDecimal(10),
                NonRecoverableAmount: reader.GetDecimal(11),
                ReportingCategory: reader.GetString(12)));
        }

        return rows;
    }

    private static async Task<SalesTaxCodeUpsertInput> ResolveTaxCodeInputAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        SalesTaxCodeUpsertInput input,
        CancellationToken cancellationToken)
    {
        var components = new List<SalesTaxCodeComponentUpsertInput>(input.Components.Count);
        foreach (var component in input.Components)
        {
            if (component.TaxRuleId is not Guid taxRuleId)
            {
                components.Add(component);
                continue;
            }

            var rule = await ReadTaxRuleAsync(
                connection,
                transaction,
                companyId,
                taxRuleId,
                cancellationToken).ConfigureAwait(false);
            if (rule is null)
            {
                throw new InvalidOperationException("Selected tax rule does not exist for this company.");
            }

            components.Add(component with
            {
                RatePercent = rule.CurrentRatePercent,
                TaxType = rule.TaxType,
                Recoverability = rule.Recoverability,
                AppliesTo = rule.AppliesTo,
                RegistrationNumber = string.IsNullOrWhiteSpace(component.RegistrationNumber)
                    ? rule.RegistrationNumber
                    : component.RegistrationNumber
            });
        }

        return input with { Components = components };
    }

    private async Task ReplaceComponentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid taxCodeId,
        SalesTaxCodeUpsertInput input,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                DELETE FROM sales_tax_code_components
                 WHERE company_id = @company_id
                   AND tax_code_id = @tax_code_id;
""";
            deleteCommand.Parameters.AddWithValue("company_id", companyId.Value);
            deleteCommand.Parameters.AddWithValue("tax_code_id", taxCodeId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < input.Components.Count; index++)
        {
            var component = input.Components[index];
            var sequence = index + 1;
            var componentId = component.TaxRuleId;
            var recoverability = component.Recoverability;

            if (componentId is Guid taxRuleId)
            {
                var rule = await ReadTaxRuleAsync(
                    connection,
                    transaction,
                    companyId,
                    taxRuleId,
                    cancellationToken).ConfigureAwait(false);
                if (rule is null)
                {
                    throw new InvalidOperationException("Selected tax rule does not exist for this company.");
                }

                recoverability = rule.Recoverability;
            }
            else
            {
                var componentCode = input.Components.Count == 1
                    ? input.Code.Trim()
                    : $"{input.Code.Trim()}_{sequence:00}";
                var componentName = input.Components.Count == 1
                    ? input.Name.Trim()
                    : $"{input.Name.Trim()} {sequence}";
                componentId = await UpsertComponentAsync(
                    connection,
                    transaction,
                    companyId,
                    componentCode,
                    componentName,
                    component.TaxType,
                    component.AppliesTo,
                    SalesTaxTreatment.Taxable,
                    component.Recoverability,
                    component.RegistrationNumber,
                    isActive: true,
                    now,
                    cancellationToken).ConfigureAwait(false);

                await UpsertCurrentRateAsync(
                    connection,
                    transaction,
                    companyId,
                    componentId.Value,
                    component.RatePercent,
                    cancellationToken).ConfigureAwait(false);
            }

            await using var linkCommand = connection.CreateCommand();
            linkCommand.Transaction = transaction;
            linkCommand.CommandText = """
                INSERT INTO sales_tax_code_components (
                    company_id, tax_code_id, tax_component_id, sequence, applies_to,
                    compound_mode, recoverability_override, recoverable_percent, registration_number, created_at)
                VALUES (
                    @company_id, @tax_code_id, @tax_component_id, @sequence, @applies_to,
                    'none', @recoverability, 100, @registration_number, @now);
""";
            linkCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linkCommand.Parameters.AddWithValue("tax_code_id", taxCodeId);
            linkCommand.Parameters.AddWithValue("tax_component_id", componentId.Value);
            linkCommand.Parameters.AddWithValue("sequence", sequence);
            linkCommand.Parameters.AddWithValue("applies_to", component.AppliesTo);
            linkCommand.Parameters.AddWithValue("recoverability", recoverability);
            linkCommand.Parameters.AddWithValue("registration_number", DbValue(component.RegistrationNumber));
            linkCommand.Parameters.AddWithValue("now", now);
            await linkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Guid> UpsertComponentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string code,
        string name,
        string taxType,
        string appliesTo,
        string treatment,
        string recoverability,
        string? registrationNumber,
        bool isActive,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sales_tax_components (
                company_id, code, name, tax_type, applies_to, treatment, recoverability,
                registration_number, is_active, created_at, updated_at)
            VALUES (
                @company_id, @code, @name, @tax_type, @applies_to, @treatment, @recoverability,
                @registration_number, @is_active, @now, @now)
            ON CONFLICT (company_id, code)
            DO UPDATE SET
                name = EXCLUDED.name,
                tax_type = EXCLUDED.tax_type,
                applies_to = EXCLUDED.applies_to,
                treatment = EXCLUDED.treatment,
                recoverability = EXCLUDED.recoverability,
                registration_number = EXCLUDED.registration_number,
                is_active = EXCLUDED.is_active,
                updated_at = EXCLUDED.updated_at
            RETURNING id;
""";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("tax_type", taxType);
        command.Parameters.AddWithValue("applies_to", appliesTo);
        command.Parameters.AddWithValue("treatment", treatment);
        command.Parameters.AddWithValue("recoverability", recoverability);
        command.Parameters.AddWithValue("registration_number", DbValue(registrationNumber));
        command.Parameters.AddWithValue("is_active", isActive);
        command.Parameters.AddWithValue("now", now);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid id ? id : throw new InvalidOperationException("Sales tax component upsert returned no id.");
    }

    private static async Task ReplaceRuleAccountMappingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid taxRuleId,
        SalesTaxRuleUpsertInput input,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                DELETE FROM sales_tax_account_mappings
                 WHERE company_id = @company_id
                   AND tax_component_id = @tax_component_id;
                """;
            deleteCommand.Parameters.AddWithValue("company_id", companyId.Value);
            deleteCommand.Parameters.AddWithValue("tax_component_id", taxRuleId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (input.PayableAccountId is null && input.RecoverableAccountId is null)
        {
            return;
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO sales_tax_account_mappings (
                company_id, tax_component_id, applies_to,
                payable_account_id, recoverable_account_id, nonrecoverable_account_id,
                clearing_account_id, created_at, updated_at)
            VALUES (
                @company_id, @tax_component_id, @applies_to,
                @payable_account_id, @recoverable_account_id, NULL,
                NULL, @now, @now);
            """;
        insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
        insertCommand.Parameters.AddWithValue("tax_component_id", taxRuleId);
        insertCommand.Parameters.AddWithValue("applies_to", input.AppliesTo);
        insertCommand.Parameters.AddWithValue("payable_account_id", (object?)input.PayableAccountId ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("recoverable_account_id", (object?)input.RecoverableAccountId ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("now", now);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertCurrentRateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid componentId,
        decimal ratePercent,
        CancellationToken cancellationToken)
    {
        await using var expireCommand = connection.CreateCommand();
        expireCommand.Transaction = transaction;
        expireCommand.CommandText = """
            UPDATE sales_tax_component_rates
               SET effective_to = CURRENT_DATE - 1
             WHERE company_id = @company_id
               AND tax_component_id = @tax_component_id
               AND effective_to IS NULL;
""";
        expireCommand.Parameters.AddWithValue("company_id", companyId.Value);
        expireCommand.Parameters.AddWithValue("tax_component_id", componentId);
        await expireCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO sales_tax_component_rates (
                company_id, tax_component_id, rate_percent, effective_from, source)
            VALUES (
                @company_id, @tax_component_id, @rate_percent, CURRENT_DATE, 'manual');
""";
        insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
        insertCommand.Parameters.AddWithValue("tax_component_id", componentId);
        insertCommand.Parameters.AddWithValue("rate_percent", ratePercent);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SalesTaxComponentRecord> GetTaxRuleOrThrowAsync(
        CompanyId companyId,
        Guid taxRuleId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rule = await ReadTaxRuleAsync(connection, transaction: null, companyId, taxRuleId, cancellationToken).ConfigureAwait(false);
        return rule ?? throw new InvalidOperationException("Sales tax rule was saved but could not be reloaded.");
    }

    private static async Task<SalesTaxComponentRecord?> ReadTaxRuleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid taxRuleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stc.id,
                   stc.company_id::text,
                   stc.authority_id,
                   stc.code,
                   stc.name,
                   stc.tax_type,
                   stc.applies_to,
                   stc.treatment,
                   stc.recoverability,
                   COALESCE(rate.rate_percent, 0) AS current_rate_percent,
                   mapping.payable_account_id,
                   mapping.recoverable_account_id,
                   stc.registration_number,
                   rate.effective_from,
                   rate.effective_to,
                   stc.is_active
              FROM sales_tax_components stc
         LEFT JOIN LATERAL (
                SELECT r.rate_percent, r.effective_from, r.effective_to
                  FROM sales_tax_component_rates r
                 WHERE r.company_id = stc.company_id
                   AND r.tax_component_id = stc.id
                   AND r.effective_from <= CURRENT_DATE
                   AND (r.effective_to IS NULL OR r.effective_to >= CURRENT_DATE)
              ORDER BY r.effective_from DESC
                 LIMIT 1
         ) rate ON TRUE
         LEFT JOIN LATERAL (
                SELECT m.payable_account_id, m.recoverable_account_id
                  FROM sales_tax_account_mappings m
                 WHERE m.company_id = stc.company_id
                   AND m.tax_component_id = stc.id
                   AND m.applies_to = stc.applies_to
              ORDER BY m.updated_at DESC, m.created_at DESC
                 LIMIT 1
         ) mapping ON TRUE
             WHERE stc.company_id = @company_id
               AND stc.id = @id;
""";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", taxRuleId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapTaxRule(reader)
            : null;
    }

    private static SalesTaxComponentRecord MapTaxRule(NpgsqlDataReader reader) =>
        new(
            Id: reader.GetGuid(0),
            CompanyId: CompanyId.Parse(reader.GetString(1)),
            AuthorityId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
            Code: reader.GetString(3),
            Name: reader.GetString(4),
            TaxType: reader.GetString(5),
            AppliesTo: reader.GetString(6),
            Treatment: reader.GetString(7),
            Recoverability: reader.GetString(8),
            CurrentRatePercent: reader.GetDecimal(9),
            PayableAccountId: reader.IsDBNull(10) ? null : reader.GetGuid(10),
            RecoverableAccountId: reader.IsDBNull(11) ? null : reader.GetGuid(11),
            RegistrationNumber: reader.IsDBNull(12) ? null : reader.GetString(12),
            EffectiveFrom: reader.IsDBNull(13) ? null : reader.GetFieldValue<DateOnly>(13),
            EffectiveTo: reader.IsDBNull(14) ? null : reader.GetFieldValue<DateOnly>(14),
            IsActive: reader.GetBoolean(15));

    private static async Task RefreshLegacyCodeRatesForRuleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid taxRuleId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tax_codes tc
               SET rate_percent = rollup.rate_percent,
                   updated_at = @now
              FROM (
                    SELECT tc_inner.id,
                           SUM(
                               CASE
                                   WHEN tc_inner.applies_to = 'sales'
                                        AND tcc.applies_to IN ('sales', 'both')
                                   THEN COALESCE(rate.rate_percent, 0)
                                   WHEN tc_inner.applies_to = 'purchase'
                                        AND tcc.applies_to IN ('purchase', 'both')
                                   THEN COALESCE(rate.rate_percent, 0)
                                   WHEN tc_inner.applies_to = 'both'
                                   THEN COALESCE(rate.rate_percent, 0)
                                   ELSE 0
                               END
                           ) AS rate_percent
                      FROM tax_codes tc_inner
                      JOIN sales_tax_code_components tcc
                        ON tcc.company_id = tc_inner.company_id::text
                       AND tcc.tax_code_id = tc_inner.id
                 LEFT JOIN LATERAL (
                        SELECT r.rate_percent
                          FROM sales_tax_component_rates r
                         WHERE r.company_id = tcc.company_id
                           AND r.tax_component_id = tcc.tax_component_id
                           AND r.effective_from <= CURRENT_DATE
                           AND (r.effective_to IS NULL OR r.effective_to >= CURRENT_DATE)
                      ORDER BY r.effective_from DESC
                         LIMIT 1
                 ) rate ON TRUE
                     WHERE tc_inner.company_id::text = @company_id
                       AND tc_inner.id IN (
                            SELECT tax_code_id
                              FROM sales_tax_code_components
                             WHERE company_id = @company_id
                               AND tax_component_id = @tax_rule_id)
                  GROUP BY tc_inner.id
              ) rollup
             WHERE tc.company_id::text = @company_id
               AND tc.id = rollup.id;
""";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("tax_rule_id", taxRuleId);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SalesTaxCodeRecord> GetTaxCodeOrThrowAsync(
        CompanyId companyId,
        Guid taxCodeId,
        CancellationToken cancellationToken)
    {
        var rows = await ListTaxCodesAsync(companyId, includeInactive: true, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(row => row.Id == taxCodeId)
            ?? throw new InvalidOperationException("Sales tax code was saved but could not be reloaded.");
    }

    private static decimal AggregateRate(IReadOnlyList<SalesTaxCodeComponentUpsertInput> components, string appliesTo) =>
        components
            .Where(component => AppliesToMatches(component.AppliesTo, appliesTo))
            .Sum(component => component.RatePercent);

    private static bool AppliesToMatches(string componentAppliesTo, string taxCodeAppliesTo) =>
        taxCodeAppliesTo switch
        {
            TaxCodeAppliesTo.Sales => componentAppliesTo is TaxCodeAppliesTo.Sales or TaxCodeAppliesTo.Both,
            TaxCodeAppliesTo.Purchase => componentAppliesTo is TaxCodeAppliesTo.Purchase or TaxCodeAppliesTo.Both,
            _ => true,
        };

    private static bool HasRecoverablePurchaseComponent(SalesTaxCodeUpsertInput input) =>
        input.Components.Any(component =>
            AppliesToMatches(component.AppliesTo, TaxCodeAppliesTo.Purchase) &&
            component.Recoverability == SalesTaxRecoverability.Recoverable);

    private static bool HasSalesTaxComponent(SalesTaxCodeUpsertInput input) =>
        input.Components.Any(component =>
            AppliesToMatches(component.AppliesTo, TaxCodeAppliesTo.Sales) &&
            component.RatePercent > 0m);

    private static async Task<Guid> ResolveRecoverableTaxAccountAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        SalesTaxCodeUpsertInput input,
        CancellationToken cancellationToken)
    {
        var mapped = await ReadMappedTaxAccountAsync(
            connection,
            companyId,
            input.Components
                .Where(component =>
                    component.TaxRuleId is not null &&
                    AppliesToMatches(component.AppliesTo, TaxCodeAppliesTo.Purchase) &&
                    component.Recoverability == SalesTaxRecoverability.Recoverable)
                .Select(component => component.TaxRuleId!.Value)
                .ToArray(),
            "recoverable_account_id",
            cancellationToken).ConfigureAwait(false);

        return mapped ?? await ReadRecoverableTaxAccountAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid> ResolvePayableTaxAccountAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        SalesTaxCodeUpsertInput input,
        CancellationToken cancellationToken)
    {
        var mapped = await ReadMappedTaxAccountAsync(
            connection,
            companyId,
            input.Components
                .Where(component =>
                    component.TaxRuleId is not null &&
                    AppliesToMatches(component.AppliesTo, TaxCodeAppliesTo.Sales) &&
                    component.RatePercent > 0m)
                .Select(component => component.TaxRuleId!.Value)
                .ToArray(),
            "payable_account_id",
            cancellationToken).ConfigureAwait(false);

        return mapped ?? await ReadPayableTaxAccountAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid?> ReadMappedTaxAccountAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        IReadOnlyList<Guid> taxRuleIds,
        string accountColumnName,
        CancellationToken cancellationToken)
    {
        if (taxRuleIds.Count == 0)
        {
            return null;
        }

        var columnName = accountColumnName is "payable_account_id" or "recoverable_account_id"
            ? accountColumnName
            : throw new ArgumentOutOfRangeException(nameof(accountColumnName));

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {columnName}
              FROM sales_tax_account_mappings
             WHERE company_id = @company_id
               AND tax_component_id = ANY(@tax_rule_ids)
               AND {columnName} IS NOT NULL
            ORDER BY updated_at DESC, created_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add("tax_rule_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid).Value = taxRuleIds.ToArray();
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is Guid id ? id : null;
    }

    private static async Task<Guid> ReadRecoverableTaxAccountAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
              FROM accounts
             WHERE company_id = @company_id
               AND root_type IN ('asset', 'liability')
               AND detail_type = 'tax'
               AND is_active = TRUE
             ORDER BY
               CASE
                 WHEN code = '13700' THEN 0
                 WHEN root_type = 'asset' THEN 1
                 WHEN system_key = 'tax:payable' THEN 2
                 WHEN system_role = 'tax_payable' THEN 3
                 WHEN code = '25000' THEN 4
                 ELSE 5
               END,
               code
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is Guid id
            ? id
            : throw new InvalidOperationException("Recoverable purchase tax requires an active tax account.");
    }

    private static async Task<Guid> ReadPayableTaxAccountAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
              FROM accounts
             WHERE company_id = @company_id
               AND root_type = 'liability'
               AND detail_type = 'tax'
               AND is_active = TRUE
            ORDER BY
               CASE
                 WHEN system_key = 'tax:payable' THEN 0
                 WHEN system_role = 'tax_payable' THEN 1
                 WHEN code = '25000' THEN 2
                 WHEN name ILIKE '%Sales Tax Payable%' THEN 3
                 ELSE 4
               END,
               code
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is Guid id
            ? id
            : throw new InvalidOperationException("Sales tax codes with sales tax require an active liability tax account.");
    }

    private static object DbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string GenerateEntityNumber()
    {
        var year = DateTime.UtcNow.Year;
        var seed = Random.Shared.Next(0, (int)EntityNumber.MaxOrdinal + 1);
        return EntityNumber.Create(year, seed).Value;
    }

    private sealed record SalesTaxCodeBuilder(
        Guid Id,
        CompanyId CompanyId,
        string EntityNumber,
        string Code,
        string Name,
        decimal RatePercent,
        string AppliesTo,
        string? RegistrationNumber,
        bool IsActive,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        public List<SalesTaxCodeComponentRecord> Components { get; } = new();

        public SalesTaxCodeRecord ToRecord()
        {
            var components = Components.Count == 0
                ? new[]
                {
                    new SalesTaxCodeComponentRecord(
                        TaxCodeId: Id,
                        TaxComponentId: null,
                        Code: Code,
                        Name: Name,
                        TaxType: "sales_tax",
                        AppliesTo: AppliesTo,
                        RatePercent: RatePercent,
                        Sequence: 1,
                        CompoundMode: "none",
                        Treatment: RatePercent == 0m ? SalesTaxTreatment.Exempt : SalesTaxTreatment.Taxable,
                        Recoverability: AppliesTo is TaxCodeAppliesTo.Purchase or TaxCodeAppliesTo.Both && RatePercent > 0m
                            ? SalesTaxRecoverability.Recoverable
                            : SalesTaxRecoverability.NotApplicable,
                        RecoverablePercent: 100m,
                        RegistrationNumber: RegistrationNumber)
                }
                : Components.ToArray();

            var salesRate = components
                .Where(c => c.AppliesTo is TaxCodeAppliesTo.Sales or TaxCodeAppliesTo.Both)
                .Sum(c => c.RatePercent);
            var purchaseRate = components
                .Where(c => c.AppliesTo is TaxCodeAppliesTo.Purchase or TaxCodeAppliesTo.Both)
                .Sum(c => c.RatePercent);

            return new SalesTaxCodeRecord(
                Id: Id,
                CompanyId: CompanyId,
                EntityNumber: EntityNumber,
                Code: Code,
                Name: Name,
                Treatment: components.Any(c => c.Treatment == SalesTaxTreatment.Taxable)
                    ? SalesTaxTreatment.Taxable
                    : components[0].Treatment,
                AppliesTo: AppliesTo,
                SalesRatePercent: salesRate,
                PurchaseRatePercent: purchaseRate,
                RegistrationNumber: RegistrationNumber,
                IsGroup: components.Length > 1,
                IsActive: IsActive,
                Components: components,
                CreatedAt: CreatedAt,
                UpdatedAt: UpdatedAt);
        }
    }
}
