namespace Web.Shell.Services;

public static class ShellCompanyAccountCatalogRules
{
    public static ShellCompanyAccountCatalogRuleResult ValidateCreate(
        ShellCompanyBankAccountCreateRequest? request,
        ShellCompanyAccountCatalogSummary? summary)
    {
        if (request is null)
        {
            return Fail("missing_request", "Bank account input is required.");
        }

        if (summary is null)
        {
            return Fail("missing_summary", "Company bank and cash context is unavailable.");
        }

        var normalizedName = request.Name.Trim();
        var normalizedCurrencyCode = request.CurrencyCode.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Fail("missing_name", "Bank account name is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedCurrencyCode))
        {
            return Fail("missing_currency", "Bank account currency is required.");
        }

        if (!summary.EnabledCurrencies.Any(item => string.Equals(item.Code, normalizedCurrencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("invalid_currency", "The selected currency is not enabled for this company.");
        }

        if (summary.ActiveBankAccounts.Concat(summary.InactiveBankAccounts).Count() >= 100)
        {
            return Fail("bank_family_exhausted", "No free bank account code remains in the reserved 1000-1099 family.");
        }

        if (summary.ActiveBankAccounts.Concat(summary.InactiveBankAccounts).Any(
                item => string.Equals(item.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.CurrencyCode.Trim(), normalizedCurrencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("duplicate_name_currency", "A bank account with the same name and currency already exists for this company.");
        }

        return Success();
    }

    public static ShellCompanyAccountCatalogRuleResult ValidateActiveStateChange(
        ShellCompanyBankAccountSummary? account,
        bool isActive,
        ShellCompanyAccountCatalogSummary? summary)
    {
        if (account is null)
        {
            return Fail("missing_account", "The selected bank account could not be found.");
        }

        if (summary is null)
        {
            return Fail("missing_summary", "Company bank and cash context is unavailable.");
        }

        if (account.IsActive == isActive)
        {
            return Success();
        }

        if (!isActive)
        {
            if (account.IsSystemDefault)
            {
                return Fail("default_bank_protected", "The primary starter bank account cannot be deactivated in this minimal governance flow.");
            }

            if (summary.ActiveBankAccounts.Count <= 1)
            {
                return Fail("last_active_bank_protected", "At least one active bank account must remain available for settlement workflows.");
            }
        }

        return Success();
    }

    private static ShellCompanyAccountCatalogRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellCompanyAccountCatalogRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellCompanyAccountCatalogRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
