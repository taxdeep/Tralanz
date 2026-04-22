using System.Text.Json;
using Citus.Platform.Infrastructure.Persistence;
using Npgsql;

namespace Web.Shell.Services;

public sealed class WebShellCompanyOnboardingStore(PlatformPostgresConnectionFactory connectionFactory) : IWebShellCompanyOnboardingStore
{
    public async Task<WebShellCompanyOnboardingSummary?> GetAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, companyId, cancellationToken);
    }

    public async Task<WebShellCompanyOnboardingSummary?> AcknowledgeAsync(
        Guid companyId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadAsync(connection, companyId, transaction, cancellationToken);
        if (current is null || !current.RequiresOnboarding)
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update company_settings
                set profile =
                    jsonb_set(
                      jsonb_set(
                        profile,
                        '{FirstBusinessLoginAcknowledgedAtUtc}',
                        to_jsonb(@acknowledged_at::timestamptz),
                        true),
                      '{FirstBusinessLoginAcknowledgedByUserId}',
                      to_jsonb(@acknowledged_by::uuid),
                      true),
                    updated_at = @acknowledged_at
                where company_id = @company_id;
                """;
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("acknowledged_at", DateTimeOffset.UtcNow);
            command.Parameters.AddWithValue("acknowledged_by", userId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var refreshed = await ReadAsync(connection, companyId, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return refreshed;
    }

    private static async Task<WebShellCompanyOnboardingSummary?> ReadAsync(
        NpgsqlConnection connection,
        Guid companyId,
        CancellationToken cancellationToken) =>
        await ReadAsync(connection, companyId, transaction: null, cancellationToken);

    private static async Task<WebShellCompanyOnboardingSummary?> ReadAsync(
        NpgsqlConnection connection,
        Guid companyId,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              c.id,
              c.legal_name as company_name,
              c.entity_number as company_code,
              c.entity_type,
              c.industry,
              c.base_currency_code,
              c.account_code_length,
              cs.profile
            from companies c
            left join company_settings cs on cs.company_id = c.id
            where c.id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var profile = ParseJsonElement(reader, "profile");
        var readiness = await ReadProvisioningReadinessAsync(connection, companyId, transaction, cancellationToken);

        return new WebShellCompanyOnboardingSummary
        {
            CompanyId = reader.GetGuid(reader.GetOrdinal("id")),
            CompanyName = reader.GetString(reader.GetOrdinal("company_name")),
            CompanyCode = reader.GetString(reader.GetOrdinal("company_code")),
            EntityType = reader.GetString(reader.GetOrdinal("entity_type")),
            Industry = reader.GetString(reader.GetOrdinal("industry")),
            BaseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code")),
            AccountCodeLength = reader.GetInt32(reader.GetOrdinal("account_code_length")),
            OwnerDisplayName = ReadString(profile, "ProvisionedOwnerDisplayName"),
            OwnerEmail = ReadString(profile, "ProvisionedOwnerEmail"),
            TemplateKey = ReadString(profile, "TemplateKey"),
            TemplateVersion = ReadString(profile, "TemplateVersion"),
            FirstTimeSetupCompletedAtUtc = ReadDateTimeOffset(profile, "FirstTimeSetupCompletedAtUtc"),
            FirstBusinessLoginAcknowledgedAtUtc = ReadDateTimeOffset(profile, "FirstBusinessLoginAcknowledgedAtUtc"),
            StarterAccountCodes = ReadStringArray(profile, "StarterAccountCodes"),
            ReservedFamilies = ReadReservedFamilies(profile),
            StarterAccountCount = readiness.StarterAccountCount,
            HasPrimaryBook = readiness.HasPrimaryBook,
            StarterBankAccountCode = readiness.StarterBankAccountCode,
            HasReceivableControlAccount = readiness.HasReceivableControlAccount,
            HasPayableControlAccount = readiness.HasPayableControlAccount,
            ActiveTaxCodeCount = readiness.ActiveTaxCodeCount
        };
    }

    private static async Task<ProvisioningReadinessSnapshot> ReadProvisioningReadinessAsync(
        NpgsqlConnection connection,
        Guid companyId,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              (select count(*)::int from accounts a where a.company_id = @company_id and a.is_active) as starter_account_count,
              exists(
                select 1
                from company_books b
                where b.company_id = @company_id
                  and b.is_active
                  and b.is_primary) as has_primary_book,
              coalesce((
                select a.code
                from accounts a
                where a.company_id = @company_id
                  and a.is_active
                  and a.detail_type = 'bank'
                order by a.code asc
                limit 1), '') as starter_bank_account_code,
              exists(
                select 1
                from accounts a
                where a.company_id = @company_id
                  and a.is_active
                  and a.system_role = 'accounts_receivable') as has_receivable_control_account,
              exists(
                select 1
                from accounts a
                where a.company_id = @company_id
                  and a.is_active
                  and a.system_role = 'accounts_payable') as has_payable_control_account,
              case
                when to_regclass('public.tax_codes') is null then 0
                else (
                  select count(*)::int
                  from tax_codes t
                  where t.company_id = @company_id
                    and t.is_active)
              end as active_tax_code_count;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ProvisioningReadinessSnapshot();
        }

        return new ProvisioningReadinessSnapshot
        {
            StarterAccountCount = reader.GetInt32(reader.GetOrdinal("starter_account_count")),
            HasPrimaryBook = reader.GetBoolean(reader.GetOrdinal("has_primary_book")),
            StarterBankAccountCode = reader.GetString(reader.GetOrdinal("starter_bank_account_code")),
            HasReceivableControlAccount = reader.GetBoolean(reader.GetOrdinal("has_receivable_control_account")),
            HasPayableControlAccount = reader.GetBoolean(reader.GetOrdinal("has_payable_control_account")),
            ActiveTaxCodeCount = reader.GetInt32(reader.GetOrdinal("active_tax_code_count"))
        };
    }

    private static JsonElement ParseJsonElement(NpgsqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return default;
        }

        var raw = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        using var document = System.Text.Json.JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static string ReadString(JsonElement profile, string propertyName)
    {
        if (profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString() ?? string.Empty;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement profile, string propertyName)
    {
        if (profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(property.GetString(), out var value)
            ? value
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement profile, string propertyName)
    {
        if (profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadReservedFamilies(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty("ReservedFamilies", out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var direct = item.GetString();
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    values.Add(direct);
                }

                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("CodeRange", out var codeRange) &&
                codeRange.ValueKind == JsonValueKind.String)
            {
                var value = codeRange.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value!);
                }
            }
        }

        return values;
    }

    private sealed record ProvisioningReadinessSnapshot
    {
        public int StarterAccountCount { get; init; }

        public bool HasPrimaryBook { get; init; }

        public string StarterBankAccountCode { get; init; } = string.Empty;

        public bool HasReceivableControlAccount { get; init; }

        public bool HasPayableControlAccount { get; init; }

        public int ActiveTaxCodeCount { get; init; }
    }
}
