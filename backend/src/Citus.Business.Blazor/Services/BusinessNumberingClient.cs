using Infrastructure.PostgreSQL;
using Npgsql;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Read/write helper for company-scoped business display numbering rules. Talks
/// directly to the company_numbering_sequences table because that's how the
/// settings surface was originally wired in the legacy Web.Shell host
/// (commit d5df368). The HTTP-via-Citus.Accounting.Api wrapper is a follow-up
/// item — see the P3 settings work.
/// </summary>
public sealed class BusinessNumberingClient(PostgreSqlConnectionFactory connections)
{
    private static readonly IReadOnlyList<NumberingDefinition> KnownDefinitions =
    [
        new("journal_entry", "Journal Entry", "journal-entry-display", "JE-", 6, "GL", true),
        new("invoice", "Invoice", "invoice-display", "INV-", 6, "AR", true),
        new("receive_payment", "Receive Payment", "receive-payment-display", "RCP-", 6, "AR", true),
        new("credit_note", "Credit Note", "credit-note-display", "CN-", 6, "AR", true),
        new("credit_application", "Credit Application", "credit-application-display", "CA-", 6, "AR", true),
        new("bill", "Bill", "bill-display", "BILL-", 6, "AP", true),
        new("pay_bill", "Pay Bill", "pay-bill-display", "PB-", 6, "AP", true),
        new("vendor_credit", "Vendor Credit", "vendor-credit-display", "VC-", 6, "AP", true),
        new("vendor_credit_application", "Vendor Credit Application", "vendor-credit-application-display", "VCA-", 6, "AP", true),
        new("purchase_order", "Purchase Order", "purchase-order-display", "PO-", 6, "AP", true),
        new("receipt", "Receipt", "receipt-display", "RECEIPT-", 6, "Inventory", true),
        new("manual_journal", "Manual Journal Draft", "manual-journal-display", "MJ-", 6, "GL", true),
        new("fx_revaluation", "FX Revaluation", "fx-revaluation-display", "FXRV-", 6, "GL", true),
        new("quote", "Quote", "quote-display", "QT-", 6, "AR", false),
        new("sales_order", "Sales Order", "sales-order-display", "SO-", 6, "AR", false),
        new("customer", "Customer", "customer-display", "CUS-", 6, "AR", false),
        new("vendor", "Vendor", "vendor-display", "VEN-", 6, "AP", false),
        new("expense", "Expense", "expense-display", "EXP-", 6, "AP", false)
    ];

    public async Task<BusinessNumberingSummary> GetSummaryAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        await SeedKnownRulesAsync(connection, companyId, cancellationToken);

        var rows = await LoadRowsAsync(connection, companyId, cancellationToken);
        var definitionsByScope = KnownDefinitions.ToDictionary(static item => item.ScopeKey, StringComparer.OrdinalIgnoreCase);
        var configuredRows = rows.ToDictionary(static item => item.ScopeKey, StringComparer.OrdinalIgnoreCase);
        var merged = new List<BusinessNumberingRuleSummary>();

        foreach (var definition in KnownDefinitions)
        {
            configuredRows.TryGetValue(definition.ScopeKey, out var row);
            var prefix = row?.Prefix ?? definition.DefaultPrefix;
            var padding = row?.Padding ?? definition.DefaultPadding;
            var nextNumber = row?.NextNumber ?? 1;

            merged.Add(new BusinessNumberingRuleSummary(
                definition.Module,
                definition.Label,
                definition.ScopeKey,
                definition.Family,
                prefix,
                padding,
                nextNumber,
                row?.Enabled ?? true,
                BuildPreview(prefix, padding, nextNumber),
                definition.IsRuntimeConnected,
                definition.IsRuntimeConnected ? "Runtime" : "Planned"));
        }

        foreach (var row in rows.Where(row => !definitionsByScope.ContainsKey(row.ScopeKey)))
        {
            merged.Add(new BusinessNumberingRuleSummary(
                row.ScopeKey,
                FormatUnknownLabel(row.ScopeKey),
                row.ScopeKey,
                "Custom",
                row.Prefix,
                row.Padding,
                row.NextNumber,
                row.Enabled,
                BuildPreview(row.Prefix, row.Padding, row.NextNumber),
                true,
                "Custom"));
        }

        return new BusinessNumberingSummary
        {
            CompanyId = companyId,
            Rules = merged
                .OrderByDescending(static item => item.IsRuntimeConnected)
                .ThenBy(static item => item.Family, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public async Task<BusinessNumberingRuleSummary> SaveAsync(
        CompanyId companyId,
        BusinessNumberingRuleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var definition = KnownDefinitions.FirstOrDefault(
            item => string.Equals(item.ScopeKey, request.ScopeKey, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            throw new InvalidOperationException("Only known numbering modules can be edited from this settings page.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into company_numbering_sequences (
              company_id,
              scope_key,
              prefix,
              next_number,
              padding,
              suggestion_enabled,
              updated_at
            )
            values (
              @company_id,
              @scope_key,
              @prefix,
              @next_number,
              @padding,
              @enabled,
              now()
            )
            on conflict (company_id, scope_key) do update
              set prefix = excluded.prefix,
                  next_number = excluded.next_number,
                  padding = excluded.padding,
                  suggestion_enabled = excluded.suggestion_enabled,
                  updated_at = now()
            returning prefix, next_number, padding, suggestion_enabled;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("scope_key", definition.ScopeKey);
        command.Parameters.AddWithValue("prefix", request.Prefix.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("next_number", request.NextNumber);
        command.Parameters.AddWithValue("padding", request.Padding);
        command.Parameters.AddWithValue("enabled", request.Enabled);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        var prefix = reader.GetString(reader.GetOrdinal("prefix"));
        var nextNumber = reader.GetInt64(reader.GetOrdinal("next_number"));
        var padding = reader.GetInt16(reader.GetOrdinal("padding"));
        var enabled = reader.GetBoolean(reader.GetOrdinal("suggestion_enabled"));

        return new BusinessNumberingRuleSummary(
            definition.Module,
            definition.Label,
            definition.ScopeKey,
            definition.Family,
            prefix,
            padding,
            nextNumber,
            enabled,
            BuildPreview(prefix, padding, nextNumber),
            definition.IsRuntimeConnected,
            definition.IsRuntimeConnected ? "Runtime" : "Planned");
    }

    private static async Task EnsureTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table if not exists company_numbering_sequences (
              company_id char(7) not null references companies(id) on delete cascade,
              scope_key text not null,
              prefix text not null,
              next_number bigint not null,
              padding smallint not null,
              suggestion_enabled boolean not null default true,
              updated_at timestamptz not null default now(),
              primary key (company_id, scope_key)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedKnownRulesAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        foreach (var definition in KnownDefinitions.Where(static item => item.IsRuntimeConnected))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                insert into company_numbering_sequences (
                  company_id,
                  scope_key,
                  prefix,
                  next_number,
                  padding,
                  suggestion_enabled,
                  updated_at
                )
                values (
                  @company_id,
                  @scope_key,
                  @prefix,
                  1,
                  @padding,
                  true,
                  now()
                )
                on conflict (company_id, scope_key) do nothing;
                """;
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("scope_key", definition.ScopeKey);
            command.Parameters.AddWithValue("prefix", definition.DefaultPrefix);
            command.Parameters.AddWithValue("padding", definition.DefaultPadding);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<NumberingRow>> LoadRowsAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select scope_key, prefix, next_number, padding, suggestion_enabled
            from company_numbering_sequences
            where company_id = @company_id
            order by scope_key;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var rows = new List<NumberingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NumberingRow(
                reader.GetString(reader.GetOrdinal("scope_key")),
                reader.GetString(reader.GetOrdinal("prefix")),
                reader.GetInt64(reader.GetOrdinal("next_number")),
                reader.GetInt16(reader.GetOrdinal("padding")),
                reader.GetBoolean(reader.GetOrdinal("suggestion_enabled"))));
        }

        return rows;
    }

    private static void Validate(BusinessNumberingRuleUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ScopeKey))
        {
            throw new InvalidOperationException("Numbering scope is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Prefix) || request.Prefix.Trim().Length > 16)
        {
            throw new InvalidOperationException("Prefix is required and must be 16 characters or fewer.");
        }

        if (request.Padding is < 1 or > 12)
        {
            throw new InvalidOperationException("Padding must be between 1 and 12.");
        }

        if (request.NextNumber < 1)
        {
            throw new InvalidOperationException("Next number must be 1 or greater.");
        }
    }

    private static string BuildPreview(string prefix, short padding, long nextNumber) =>
        $"{prefix}{nextNumber.ToString().PadLeft(padding, '0')}";

    private static string FormatUnknownLabel(string scopeKey) =>
        string.Join(
            " ",
            scopeKey
                .Replace("-display", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));

    private sealed record NumberingDefinition(
        string Module,
        string Label,
        string ScopeKey,
        string DefaultPrefix,
        short DefaultPadding,
        string Family,
        bool IsRuntimeConnected);

    private sealed record NumberingRow(
        string ScopeKey,
        string Prefix,
        long NextNumber,
        short Padding,
        bool Enabled);
}

public sealed record BusinessNumberingSummary
{
    public CompanyId CompanyId { get; init; }
    public IReadOnlyList<BusinessNumberingRuleSummary> Rules { get; init; } = Array.Empty<BusinessNumberingRuleSummary>();
}

public sealed record BusinessNumberingRuleSummary(
    string Module,
    string Label,
    string ScopeKey,
    string Family,
    string Prefix,
    short Padding,
    long NextNumber,
    bool Enabled,
    string Preview,
    bool IsRuntimeConnected,
    string RuntimeStatus);

public sealed record BusinessNumberingRuleUpdateRequest(
    string ScopeKey,
    string Prefix,
    short Padding,
    long NextNumber,
    bool Enabled);
