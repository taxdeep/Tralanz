namespace Citus.Accounting.Infrastructure.Persistence;

internal static class PostgresControlAccountLookup
{
    public static async Task<Guid?> TryResolveAsync(
        PostgresCommandScope scope,
        Guid companyId,
        string controlRoleBase,
        string transactionCurrencyCode,
        string baseCurrencyCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var normalizedRoleBase = string.IsNullOrWhiteSpace(controlRoleBase)
            ? throw new ArgumentException("Control role base is required.", nameof(controlRoleBase))
            : controlRoleBase.Trim().ToLowerInvariant();
        var normalizedCurrencyCode = string.IsNullOrWhiteSpace(transactionCurrencyCode)
            ? throw new ArgumentException("Transaction currency code is required.", nameof(transactionCurrencyCode))
            : transactionCurrencyCode.Trim().ToUpperInvariant();
        var normalizedBaseCurrencyCode = string.IsNullOrWhiteSpace(baseCurrencyCode)
            ? throw new ArgumentException("Base currency code is required.", nameof(baseCurrencyCode))
            : baseCurrencyCode.Trim().ToUpperInvariant();

        var systemRole = normalizedCurrencyCode == normalizedBaseCurrencyCode
            ? normalizedRoleBase
            : $"{normalizedRoleBase}:{normalizedCurrencyCode}";
        var fallbackCode = normalizedRoleBase switch
        {
            "accounts_receivable" => normalizedCurrencyCode == normalizedBaseCurrencyCode ? "1100" : $"AR-{normalizedCurrencyCode}",
            "accounts_payable" => normalizedCurrencyCode == normalizedBaseCurrencyCode ? "2000" : $"AP-{normalizedCurrencyCode}",
            _ => throw new InvalidOperationException($"Control role '{normalizedRoleBase}' is not supported.")
        };

        await using var command = scope.CreateCommand(
            """
            select id
            from accounts
            where company_id = @company_id
              and is_active = true
              and (
                system_role = @system_role
                or code = @fallback_code
              )
            order by
              case
                when system_role = @system_role then 0
                else 1
              end,
              code
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("system_role", systemRole);
        command.Parameters.AddWithValue("fallback_code", fallbackCode);

        var resolved = await command.ExecuteScalarAsync(cancellationToken);
        return resolved is Guid accountId ? accountId : null;
    }
}
