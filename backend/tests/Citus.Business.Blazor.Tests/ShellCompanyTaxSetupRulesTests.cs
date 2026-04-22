using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellCompanyTaxSetupRulesTests
{
    private static readonly ShellCompanyTaxAccountOption[] PayableAccounts =
    [
        new()
        {
            Id = Guid.Parse("f60c55ef-07c4-4c77-b353-cb9a7305a6d6"),
            Code = "2300",
            Name = "GST Payable",
            RootType = "liability"
        }
    ];

    private static readonly ShellCompanyTaxAccountOption[] RecoverableAccounts =
    [
        new()
        {
            Id = Guid.Parse("e78d7689-6d97-4ec7-a91d-6d77327be95d"),
            Code = "1400",
            Name = "GST Recoverable",
            RootType = "asset"
        }
    ];

    [Fact]
    public void Validate_Fails_WhenSalesOnlyCodeDeclaresRecoverability()
    {
        var result = ShellCompanyTaxSetupRules.Validate(
            new ShellCompanyTaxCodeUpsertRequest
            {
                Code = "GST5",
                Name = "GST 5%",
                RatePercent = 5m,
                AppliesTo = ShellCompanyTaxSetupRules.AppliesToSales,
                RecoverabilityMode = ShellCompanyTaxSetupRules.RecoverabilityFull,
                PayableAccountId = PayableAccounts[0].Id
            },
            Array.Empty<ShellCompanyManagedTaxCodeSummary>(),
            PayableAccounts,
            RecoverableAccounts);

        Assert.False(result.Succeeded);
        Assert.Equal("sales_recoverability_forbidden", result.ErrorCode);
    }

    [Fact]
    public void Validate_Fails_WhenRecoverablePurchaseCodeHasNoRecoverableAccount()
    {
        var result = ShellCompanyTaxSetupRules.Validate(
            new ShellCompanyTaxCodeUpsertRequest
            {
                Code = "GSTREC",
                Name = "Recoverable GST",
                RatePercent = 5m,
                AppliesTo = ShellCompanyTaxSetupRules.AppliesToPurchase,
                RecoverabilityMode = ShellCompanyTaxSetupRules.RecoverabilityFull,
                PayableAccountId = PayableAccounts[0].Id
            },
            Array.Empty<ShellCompanyManagedTaxCodeSummary>(),
            PayableAccounts,
            RecoverableAccounts);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_recoverable_account", result.ErrorCode);
    }

    [Fact]
    public void Validate_Fails_WhenPayableAccountMissing()
    {
        var result = ShellCompanyTaxSetupRules.Validate(
            new ShellCompanyTaxCodeUpsertRequest
            {
                Code = "GST5",
                Name = "GST 5%",
                RatePercent = 5m,
                AppliesTo = ShellCompanyTaxSetupRules.AppliesToBoth,
                RecoverabilityMode = ShellCompanyTaxSetupRules.RecoverabilityNone
            },
            Array.Empty<ShellCompanyManagedTaxCodeSummary>(),
            PayableAccounts,
            RecoverableAccounts);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_payable_account", result.ErrorCode);
    }

    [Fact]
    public void Validate_Fails_WhenCompanyScopedCodeAlreadyExists()
    {
        var result = ShellCompanyTaxSetupRules.Validate(
            new ShellCompanyTaxCodeUpsertRequest
            {
                Code = "gst5",
                Name = "GST 5%",
                RatePercent = 5m,
                AppliesTo = ShellCompanyTaxSetupRules.AppliesToBoth,
                RecoverabilityMode = ShellCompanyTaxSetupRules.RecoverabilityNone,
                PayableAccountId = PayableAccounts[0].Id
            },
            [
                new ShellCompanyManagedTaxCodeSummary
                {
                    Id = Guid.Parse("cf63535f-ad1b-4da4-b640-c5276ddbc0af"),
                    Code = "GST5",
                    Name = "Existing GST 5%"
                }
            ],
            PayableAccounts,
            RecoverableAccounts);

        Assert.False(result.Succeeded);
        Assert.Equal("duplicate_code", result.ErrorCode);
    }

    [Fact]
    public void Validate_Succeeds_ForBothDirectionPartialRecoverability()
    {
        var result = ShellCompanyTaxSetupRules.Validate(
            new ShellCompanyTaxCodeUpsertRequest
            {
                Code = "HST13",
                Name = "HST 13%",
                RatePercent = 13m,
                AppliesTo = ShellCompanyTaxSetupRules.AppliesToBoth,
                RecoverabilityMode = ShellCompanyTaxSetupRules.RecoverabilityPartial,
                PayableAccountId = PayableAccounts[0].Id,
                RecoverableAccountId = RecoverableAccounts[0].Id
            },
            Array.Empty<ShellCompanyManagedTaxCodeSummary>(),
            PayableAccounts,
            RecoverableAccounts);

        Assert.True(result.Succeeded);
    }
}
