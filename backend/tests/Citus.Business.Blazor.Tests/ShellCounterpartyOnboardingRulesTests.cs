using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellCounterpartyOnboardingRulesTests
{
    private static readonly ShellCompanyCurrencyOption[] EnabledCurrencies =
    [
        new() { Code = "CAD", Name = "Canadian Dollar" },
        new() { Code = "USD", Name = "US Dollar" }
    ];

    [Fact]
    public void ValidateCreate_Fails_WhenCompanyNameMissing()
    {
        var result = ShellCounterpartyOnboardingRules.ValidateCreate(
            new ShellCounterpartyOnboardingCreateRequest
            {
                DisplayName = "   "
            },
            Array.Empty<ShellManagedCounterpartySummary>(),
            "CAD",
            multiCurrencyEnabled: false,
            EnabledCurrencies);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_display_name", result.ErrorCode);
    }

    [Fact]
    public void ValidateCreate_Fails_WhenMultiCurrencyCompanyOmitsCurrency()
    {
        var result = ShellCounterpartyOnboardingRules.ValidateCreate(
            new ShellCounterpartyOnboardingCreateRequest
            {
                DisplayName = "Northwind"
            },
            Array.Empty<ShellManagedCounterpartySummary>(),
            "CAD",
            multiCurrencyEnabled: true,
            EnabledCurrencies);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_currency", result.ErrorCode);
    }

    [Fact]
    public void ValidateCreate_Fails_WhenCurrencyNotEnabled()
    {
        var result = ShellCounterpartyOnboardingRules.ValidateCreate(
            new ShellCounterpartyOnboardingCreateRequest
            {
                DisplayName = "Northwind",
                CurrencyCode = "EUR"
            },
            Array.Empty<ShellManagedCounterpartySummary>(),
            "CAD",
            multiCurrencyEnabled: true,
            EnabledCurrencies);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_currency", result.ErrorCode);
    }

    [Fact]
    public void ValidateCreate_Fails_WhenSingleCurrencyCompanyUsesDifferentCurrency()
    {
        var result = ShellCounterpartyOnboardingRules.ValidateCreate(
            new ShellCounterpartyOnboardingCreateRequest
            {
                DisplayName = "Northwind",
                CurrencyCode = "USD"
            },
            Array.Empty<ShellManagedCounterpartySummary>(),
            "CAD",
            multiCurrencyEnabled: false,
            EnabledCurrencies);

        Assert.False(result.Succeeded);
        Assert.Equal("single_currency_override_forbidden", result.ErrorCode);
    }

    [Fact]
    public void ValidateCreate_Fails_WhenDuplicateCompanyNameExists()
    {
        var result = ShellCounterpartyOnboardingRules.ValidateCreate(
            new ShellCounterpartyOnboardingCreateRequest
            {
                DisplayName = "northwind"
            },
            [
                new ShellManagedCounterpartySummary
                {
                    Id = Guid.NewGuid(),
                    DisplayName = "Northwind"
                }
            ],
            "CAD",
            multiCurrencyEnabled: false,
            EnabledCurrencies);

        Assert.False(result.Succeeded);
        Assert.Equal("duplicate_display_name", result.ErrorCode);
    }

    [Fact]
    public void ValidateCreate_Succeeds_ForSingleCurrencyCompanyWithoutCurrencyInput()
    {
        var result = ShellCounterpartyOnboardingRules.ValidateCreate(
            new ShellCounterpartyOnboardingCreateRequest
            {
                DisplayName = "Northwind",
                Email = "ops@northwind.example"
            },
            Array.Empty<ShellManagedCounterpartySummary>(),
            "CAD",
            multiCurrencyEnabled: false,
            EnabledCurrencies);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateUpdate_Succeeds_WhenKeepingSameNameOnSameCounterparty()
    {
        var counterpartyId = Guid.NewGuid();
        var result = ShellCounterpartyOnboardingRules.ValidateUpdate(
            counterpartyId,
            new ShellCounterpartyOnboardingCreateRequest
            {
                DisplayName = "Northwind",
                CurrencyCode = "USD"
            },
            [
                new ShellManagedCounterpartySummary
                {
                    Id = counterpartyId,
                    DisplayName = "Northwind",
                    DefaultCurrencyCode = "USD"
                }
            ],
            "CAD",
            multiCurrencyEnabled: true,
            EnabledCurrencies);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateUpdate_Fails_WhenRenamingToAnotherExistingCompanyName()
    {
        var result = ShellCounterpartyOnboardingRules.ValidateUpdate(
            Guid.NewGuid(),
            new ShellCounterpartyOnboardingCreateRequest
            {
                DisplayName = "Northwind"
            },
            [
                new ShellManagedCounterpartySummary
                {
                    Id = Guid.NewGuid(),
                    DisplayName = "Northwind"
                }
            ],
            "CAD",
            multiCurrencyEnabled: false,
            EnabledCurrencies);

        Assert.False(result.Succeeded);
        Assert.Equal("duplicate_display_name", result.ErrorCode);
    }
}
