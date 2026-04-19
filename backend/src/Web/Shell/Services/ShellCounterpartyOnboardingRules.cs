namespace Web.Shell.Services;

public static class ShellCounterpartyOnboardingRules
{
    public static ShellCounterpartyOnboardingRuleResult ValidateCreate(
        ShellCounterpartyOnboardingCreateRequest? request,
        IReadOnlyCollection<ShellManagedCounterpartySummary> existingCounterparties,
        string baseCurrencyCode,
        bool multiCurrencyEnabled,
        IReadOnlyCollection<ShellCompanyCurrencyOption> enabledCurrencies)
        => ValidateSave(
            request,
            existingCounterparties,
            editingCounterpartyId: null,
            baseCurrencyCode,
            multiCurrencyEnabled,
            enabledCurrencies);

    public static ShellCounterpartyOnboardingRuleResult ValidateUpdate(
        Guid counterpartyId,
        ShellCounterpartyOnboardingCreateRequest? request,
        IReadOnlyCollection<ShellManagedCounterpartySummary> existingCounterparties,
        string baseCurrencyCode,
        bool multiCurrencyEnabled,
        IReadOnlyCollection<ShellCompanyCurrencyOption> enabledCurrencies)
        => ValidateSave(
            request,
            existingCounterparties,
            counterpartyId,
            baseCurrencyCode,
            multiCurrencyEnabled,
            enabledCurrencies);

    private static ShellCounterpartyOnboardingRuleResult ValidateSave(
        ShellCounterpartyOnboardingCreateRequest? request,
        IReadOnlyCollection<ShellManagedCounterpartySummary> existingCounterparties,
        Guid? editingCounterpartyId,
        string baseCurrencyCode,
        bool multiCurrencyEnabled,
        IReadOnlyCollection<ShellCompanyCurrencyOption> enabledCurrencies)
    {
        if (request is null)
        {
            return Fail("missing_request", "Counterparty input is required.");
        }

        var normalizedDisplayName = request.DisplayName.Trim();
        var normalizedBaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant();
        var normalizedCurrencyCode = request.CurrencyCode.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalizedDisplayName))
        {
            return Fail("missing_display_name", "Company name is required.");
        }

        var duplicate = existingCounterparties.FirstOrDefault(
            item => item.Id != editingCounterpartyId &&
                    string.Equals(item.DisplayName.Trim(), normalizedDisplayName, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            return Fail("duplicate_display_name", "Another counterparty already uses this company name.");
        }

        if (multiCurrencyEnabled)
        {
            if (string.IsNullOrWhiteSpace(normalizedCurrencyCode))
            {
                return Fail("missing_currency", "Currency is required when multi-currency is enabled.");
            }

            if (!enabledCurrencies.Any(item => string.Equals(item.Code, normalizedCurrencyCode, StringComparison.OrdinalIgnoreCase)))
            {
                return Fail("invalid_currency", "The selected currency is not enabled for this company.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(normalizedCurrencyCode) &&
                 !string.Equals(normalizedCurrencyCode, normalizedBaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("single_currency_override_forbidden", "This company uses the base currency only. Leave currency empty or use the base currency.");
        }

        if (!string.IsNullOrWhiteSpace(request.Email) &&
            (!request.Email.Contains('@', StringComparison.Ordinal) || request.Email.StartsWith('@') || request.Email.EndsWith('@')))
        {
            return Fail("invalid_email", "Email must be blank or contain a valid address format.");
        }

        return Success();
    }

    private static ShellCounterpartyOnboardingRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellCounterpartyOnboardingRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellCounterpartyOnboardingRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
