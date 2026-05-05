using System.Text.Json;
using Citus.Accounting.Application.Invoices;
using Citus.Accounting.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Invoices;

public sealed class PostgresInvoiceTemplateStore : IInvoiceTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly PostgresConnectionFactory _connections;

    public PostgresInvoiceTemplateStore(PostgresConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists invoice_templates (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null,
              name text not null,
              is_default boolean not null default false,
              config jsonb not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create index if not exists invoice_templates_company_idx
              on invoice_templates (company_id);

            -- A company can have at most one default template at a time.
            -- Filtered unique index — only enforces uniqueness on the
            -- defaults, not on the overall (company_id, is_default) tuple.
            create unique index if not exists invoice_templates_company_default_idx
              on invoice_templates (company_id) where is_default = true;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceTemplate>> ListByCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var rows = await ListInternalAsync(companyId, cancellationToken);
        if (rows.Count > 0)
        {
            return rows;
        }

        // Empty company on first access — seed the three starter
        // templates so the operator has something to choose from instead
        // of staring at an empty list.
        await SeedStartersAsync(companyId, cancellationToken);
        return await ListInternalAsync(companyId, cancellationToken);
    }

    public async Task<InvoiceTemplate?> GetByIdAsync(
        CompanyId companyId,
        Guid templateId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, company_id, name, is_default, config, created_at, updated_at
              from invoice_templates
             where company_id = @company_id and id = @id;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", templateId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return Map(reader);
    }

    public async Task<InvoiceTemplate?> GetDefaultAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, company_id, name, is_default, config, created_at, updated_at
              from invoice_templates
             where company_id = @company_id and is_default = true
             limit 1;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return Map(reader);
        }

        // No default yet — fall back to seeding so the very first
        // PDF/send call has something to render with.
        await reader.CloseAsync();
        await connection.CloseAsync();
        await SeedStartersAsync(companyId, cancellationToken);

        await using var retryConn = await _connections.OpenConnectionAsync(cancellationToken);
        await using var retryCmd = retryConn.CreateCommand();
        retryCmd.CommandText = sql;
        retryCmd.Parameters.AddWithValue("company_id", companyId);
        await using var retryReader = await retryCmd.ExecuteReaderAsync(cancellationToken);
        return await retryReader.ReadAsync(cancellationToken) ? Map(retryReader) : null;
    }

    public async Task<InvoiceTemplate> CreateAsync(
        CompanyId companyId,
        InvoiceTemplateUpsertRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into invoice_templates (company_id, name, is_default, config)
            values (@company_id, @name, false, @config)
            returning id, created_at, updated_at;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        AddJsonbParameter(command, "config", request.Config);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new InvoiceTemplate(
            Id: reader.GetGuid(0),
            CompanyId: companyId,
            Name: request.Name.Trim(),
            IsDefault: false,
            Config: request.Config,
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(1),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task<InvoiceTemplate?> UpdateAsync(
        CompanyId companyId,
        Guid templateId,
        InvoiceTemplateUpsertRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update invoice_templates
               set name = @name,
                   config = @config,
                   updated_at = now()
             where company_id = @company_id and id = @id
            returning id, company_id, name, is_default, config, created_at, updated_at;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", templateId);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        AddJsonbParameter(command, "config", request.Config);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return Map(reader);
    }

    public async Task<InvoiceTemplate?> SetDefaultAsync(
        CompanyId companyId,
        Guid templateId,
        CancellationToken cancellationToken)
    {
        // Atomicity: we need to clear is_default on every other template
        // for the company before setting the new one, otherwise the
        // partial unique index trips. Wrap in a single transaction so a
        // crash mid-flip doesn't leave the company with zero defaults.
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string clearSql = """
            update invoice_templates
               set is_default = false,
                   updated_at = now()
             where company_id = @company_id and is_default = true and id <> @id;
            """;

        await using (var clearCmd = connection.CreateCommand())
        {
            clearCmd.Transaction = transaction;
            clearCmd.CommandText = clearSql;
            clearCmd.Parameters.AddWithValue("company_id", companyId);
            clearCmd.Parameters.AddWithValue("id", templateId);
            await clearCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        const string setSql = """
            update invoice_templates
               set is_default = true,
                   updated_at = now()
             where company_id = @company_id and id = @id
            returning id, company_id, name, is_default, config, created_at, updated_at;
            """;

        InvoiceTemplate? result = null;
        await using (var setCmd = connection.CreateCommand())
        {
            setCmd.Transaction = transaction;
            setCmd.CommandText = setSql;
            setCmd.Parameters.AddWithValue("company_id", companyId);
            setCmd.Parameters.AddWithValue("id", templateId);
            await using var reader = await setCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                result = Map(reader);
            }
        }

        if (result is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<IReadOnlyList<InvoiceTemplate>> ListInternalAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, company_id, name, is_default, config, created_at, updated_at
              from invoice_templates
             where company_id = @company_id
             order by is_default desc, name asc;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId);

        var results = new List<InvoiceTemplate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }
        return results;
    }

    private async Task SeedStartersAsync(CompanyId companyId, CancellationToken cancellationToken)
    {
        const string insertSql = """
            insert into invoice_templates (company_id, name, is_default, config)
            values (@company_id, @name, @is_default, @config);
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Re-check inside the transaction to avoid double-seeding under
        // concurrent first access.
        const string checkSql = "select 1 from invoice_templates where company_id = @company_id limit 1;";
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.Transaction = transaction;
            checkCmd.CommandText = checkSql;
            checkCmd.Parameters.AddWithValue("company_id", companyId);
            var existing = await checkCmd.ExecuteScalarAsync(cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
        }

        var starters = new[]
        {
            ("Modern", true,  BuildModernStarter()),
            ("Classic", false, BuildClassicStarter()),
            ("Minimal", false, BuildMinimalStarter()),
        };

        foreach (var (name, isDefault, config) in starters)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = insertSql;
            cmd.Parameters.AddWithValue("company_id", companyId);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("is_default", isDefault);
            AddJsonbParameter(cmd, "config", config);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static InvoiceTemplateConfig BuildModernStarter() => InvoiceTemplateConfig.Default with
    {
        PrimaryColorHex = "#0f172a",
        AccentColorHex = "#475569",
        Greeting = "Thank you for your business.",
        FooterNote = "Thank you for your business.",
    };

    private static InvoiceTemplateConfig BuildClassicStarter() => InvoiceTemplateConfig.Default with
    {
        PrimaryColorHex = "#1e3a8a",
        AccentColorHex = "#64748b",
        Greeting = "Please find the invoice details below.",
        FooterNote = "We appreciate your continued partnership.",
    };

    private static InvoiceTemplateConfig BuildMinimalStarter() => InvoiceTemplateConfig.Default with
    {
        PrimaryColorHex = "#111827",
        AccentColorHex = "#9ca3af",
        Greeting = "Invoice details follow.",
        FooterNote = string.Empty,
        ShowTaxColumn = false,
    };

    private static InvoiceTemplate Map(Npgsql.NpgsqlDataReader reader)
    {
        var configJson = reader.GetString(4);
        var config = JsonSerializer.Deserialize<InvoiceTemplateConfig>(configJson, JsonOptions)
            ?? InvoiceTemplateConfig.Default;

        return new InvoiceTemplate(
            Id: reader.GetGuid(0),
            CompanyId: reader.GetGuid(1),
            Name: reader.GetString(2),
            IsDefault: reader.GetBoolean(3),
            Config: config,
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(5),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(6));
    }

    private static void AddJsonbParameter(
        Npgsql.NpgsqlCommand command,
        string name,
        InvoiceTemplateConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var parameter = command.Parameters.AddWithValue(name, json);
        parameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
    }
}
