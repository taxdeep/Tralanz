namespace Web.Shell.Services;

public static class ShellCompanyTaxSetupRules
{
    public const string AppliesToSales = "sales";
    public const string AppliesToPurchase = "purchase";
    public const string AppliesToBoth = "both";

    public const string RecoverabilityFull = "full";
    public const string RecoverabilityPartial = "partial";
    public const string RecoverabilityNone = "none";

    public static bool HasPurchaseScope(string appliesTo) =>
        string.Equals(appliesTo, AppliesToPurchase, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(appliesTo, AppliesToBoth, StringComparison.OrdinalIgnoreCase);

    public static ShellCompanyTaxSetupRuleResult Validate(
        ShellCompanyTaxCodeUpsertRequest? request,
        IReadOnlyCollection<ShellCompanyManagedTaxCodeSummary> existingTaxCodes,
        IReadOnlyCollection<ShellCompanyTaxAccountOption> payableAccountOptions,
        IReadOnlyCollection<ShellCompanyTaxAccountOption> recoverableAccountOptions)
    {
        if (request is null)
        {
            return Fail("missing_request", "Tax code input is required.");
        }

        var normalizedCode = request.Code.Trim().ToUpperInvariant();
        var normalizedName = request.Name.Trim();
        var normalizedAppliesTo = request.AppliesTo.Trim().ToLowerInvariant();
        var normalizedRecoverabilityMode = request.RecoverabilityMode.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return Fail("missing_code", "Tax code is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Fail("missing_name", "Tax name is required.");
        }

        if (request.RatePercent is < 0m or > 100m)
        {
            return Fail("invalid_rate", "Tax rate must be between 0 and 100 percent.");
        }

        if (!new[] { AppliesToSales, AppliesToPurchase, AppliesToBoth }.Contains(normalizedAppliesTo, StringComparer.Ordinal))
        {
            return Fail("invalid_applies_to", "Tax direction must be sales, purchase, or both.");
        }

        if (!new[] { RecoverabilityFull, RecoverabilityPartial, RecoverabilityNone }.Contains(normalizedRecoverabilityMode, StringComparer.Ordinal))
        {
            return Fail("invalid_recoverability", "Recoverability must be full, partial, or none.");
        }

        var duplicate = existingTaxCodes.FirstOrDefault(
            item => item.Id != request.Id &&
                    string.Equals(item.Code.Trim(), normalizedCode, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            return Fail("duplicate_code", "Another tax code already uses this company-scoped code.");
        }

        if (!request.PayableAccountId.HasValue)
        {
            return Fail("missing_payable_account", "A tax payable account is required.");
        }

        if (!payableAccountOptions.Any(item => item.Id == request.PayableAccountId.Value))
        {
            return Fail("invalid_payable_account", "The selected payable account is not available for this company.");
        }

        if (string.Equals(normalizedAppliesTo, AppliesToSales, StringComparison.Ordinal) &&
            !string.Equals(normalizedRecoverabilityMode, RecoverabilityNone, StringComparison.Ordinal))
        {
            return Fail("sales_recoverability_forbidden", "Sales-only tax codes must use non-recoverable mode.");
        }

        var requiresRecoverableAccount =
            HasPurchaseScope(normalizedAppliesTo) &&
            !string.Equals(normalizedRecoverabilityMode, RecoverabilityNone, StringComparison.Ordinal);

        if (requiresRecoverableAccount && !request.RecoverableAccountId.HasValue)
        {
            return Fail("missing_recoverable_account", "Recoverable or partially recoverable purchase tax requires a recoverable tax account.");
        }

        if (requiresRecoverableAccount &&
            !recoverableAccountOptions.Any(item => item.Id == request.RecoverableAccountId!.Value))
        {
            return Fail("invalid_recoverable_account", "The selected recoverable account is not available for this company.");
        }

        if (!requiresRecoverableAccount && request.RecoverableAccountId.HasValue)
        {
            return Fail("unexpected_recoverable_account", "A recoverable account should only be set when purchase recoverability is enabled.");
        }

        return Success();
    }

    private static ShellCompanyTaxSetupRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellCompanyTaxSetupRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellCompanyTaxSetupRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
