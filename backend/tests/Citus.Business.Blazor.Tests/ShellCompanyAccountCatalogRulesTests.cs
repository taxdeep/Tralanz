using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellCompanyAccountCatalogRulesTests
{
    [Fact]
    public void ValidateCreate_Fails_WhenCurrencyNotEnabled()
    {
        var summary = CreateSummary();

        var result = ShellCompanyAccountCatalogRules.ValidateCreate(
            new ShellCompanyBankAccountCreateRequest
            {
                Name = "Secondary Bank",
                CurrencyCode = "EUR"
            },
            summary);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_currency", result.ErrorCode);
    }

    [Fact]
    public void ValidateCreate_Fails_WhenDuplicateNameAndCurrencyExists()
    {
        var summary = CreateSummary();

        var result = ShellCompanyAccountCatalogRules.ValidateCreate(
            new ShellCompanyBankAccountCreateRequest
            {
                Name = "Operating Bank",
                CurrencyCode = "CAD"
            },
            summary);

        Assert.False(result.Succeeded);
        Assert.Equal("duplicate_name_currency", result.ErrorCode);
    }

    [Fact]
    public void ValidateCreate_Succeeds_ForEnabledCurrency()
    {
        var summary = CreateSummary();

        var result = ShellCompanyAccountCatalogRules.ValidateCreate(
            new ShellCompanyBankAccountCreateRequest
            {
                Name = "USD Clearing",
                CurrencyCode = "USD"
            },
            summary);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateActiveStateChange_Fails_WhenDeactivatingPrimaryBank()
    {
        var summary = CreateSummary();
        var account = summary.ActiveBankAccounts[0] with { IsSystemDefault = true };

        var result = ShellCompanyAccountCatalogRules.ValidateActiveStateChange(account, false, summary);

        Assert.False(result.Succeeded);
        Assert.Equal("default_bank_protected", result.ErrorCode);
    }

    [Fact]
    public void ValidateActiveStateChange_Fails_WhenDeactivatingLastActiveBank()
    {
        var summary = CreateSummary() with
        {
            ActiveBankAccounts =
            [
                new ShellCompanyBankAccountSummary
                {
                    Id = Guid.NewGuid(),
                    Code = "1000",
                    Name = "Operating Bank",
                    CurrencyCode = "CAD",
                    IsSystemDefault = false,
                    IsActive = true
                }
            ]
        };

        var result = ShellCompanyAccountCatalogRules.ValidateActiveStateChange(summary.ActiveBankAccounts[0], false, summary);

        Assert.False(result.Succeeded);
        Assert.Equal("last_active_bank_protected", result.ErrorCode);
    }

    [Fact]
    public void ValidateActiveStateChange_Succeeds_ForReactivation()
    {
        var summary = CreateSummary();
        var account = summary.InactiveBankAccounts[0];

        var result = ShellCompanyAccountCatalogRules.ValidateActiveStateChange(account, true, summary);

        Assert.True(result.Succeeded);
    }

    private static ShellCompanyAccountCatalogSummary CreateSummary() =>
        new()
        {
            BaseCurrencyCode = "CAD",
            AccountCodeLength = 4,
            EnabledCurrencies =
            [
                new ShellCompanyCurrencyOption { Code = "CAD", Name = "Canadian Dollar" },
                new ShellCompanyCurrencyOption { Code = "USD", Name = "US Dollar" }
            ],
            ActiveBankAccounts =
            [
                new ShellCompanyBankAccountSummary
                {
                    Id = Guid.NewGuid(),
                    Code = "1000",
                    Name = "Operating Bank",
                    CurrencyCode = "CAD",
                    IsSystemDefault = true,
                    IsActive = true
                },
                new ShellCompanyBankAccountSummary
                {
                    Id = Guid.NewGuid(),
                    Code = "1001",
                    Name = "USD Reserve",
                    CurrencyCode = "USD",
                    IsSystemDefault = false,
                    IsActive = true
                }
            ],
            InactiveBankAccounts =
            [
                new ShellCompanyBankAccountSummary
                {
                    Id = Guid.NewGuid(),
                    Code = "1002",
                    Name = "Legacy Bank",
                    CurrencyCode = "CAD",
                    IsSystemDefault = false,
                    IsActive = false
                }
            ]
        };
}
