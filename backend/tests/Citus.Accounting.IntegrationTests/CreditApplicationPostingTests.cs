using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure.Posting;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class CreditApplicationPostingTests
{
    [Fact]
    public async Task CreditApplication_BaseCurrency_BuildsOnlyArOffsetLines()
    {
        var receivableAccountId = Guid.NewGuid();
        var document = CreateCreditApplicationDocument(
            receivableAccountId,
            new CurrencyCode("USD"),
            new CurrencyCode("USD"),
            sourceCarryingAmountBase: 100m,
            targetCarryingAmountBase: 100m);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            CreateFxResolution(document),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            source =>
            {
                Assert.Equal(receivableAccountId, source.AccountId);
                Assert.Equal(100m, source.TxDebit);
                Assert.Equal(0m, source.TxCredit);
                Assert.Equal(100m, source.Debit);
                Assert.Equal(0m, source.Credit);
                Assert.Equal("settlement:credit_application_source", source.PostingRole);
                Assert.Equal(1, source.SourceLineNumber);
            },
            target =>
            {
                Assert.Equal(receivableAccountId, target.AccountId);
                Assert.Equal(0m, target.TxDebit);
                Assert.Equal(100m, target.TxCredit);
                Assert.Equal(0m, target.Debit);
                Assert.Equal(100m, target.Credit);
                Assert.Equal("settlement:credit_application_target", target.PostingRole);
                Assert.Equal(1, target.SourceLineNumber);
            });
    }

    [Fact]
    public async Task CreditApplication_ForeignCurrencyGain_BuildsCreditRealizedGain()
    {
        var receivableAccountId = Guid.NewGuid();
        var gainAccountId = Guid.NewGuid();
        var lossAccountId = Guid.NewGuid();
        var document = CreateCreditApplicationDocument(
            receivableAccountId,
            new CurrencyCode("EUR"),
            new CurrencyCode("USD"),
            sourceCarryingAmountBase: 125m,
            targetCarryingAmountBase: 120m,
            gainAccountId,
            lossAccountId);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            CreateFxResolution(document),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            source => Assert.Equal(125m, source.Debit),
            target => Assert.Equal(120m, target.Credit),
            gain =>
            {
                Assert.Equal(gainAccountId, gain.AccountId);
                Assert.Equal(0m, gain.Debit);
                Assert.Equal(5m, gain.Credit);
                Assert.Equal("fx:realized_gain", gain.PostingRole);
                Assert.Equal(1, gain.SourceLineNumber);
            });
    }

    [Fact]
    public async Task VendorCreditApplication_BaseCurrency_BuildsOnlyApOffsetLines()
    {
        var payableAccountId = Guid.NewGuid();
        var document = CreateVendorCreditApplicationDocument(
            payableAccountId,
            new CurrencyCode("USD"),
            new CurrencyCode("USD"),
            sourceCarryingAmountBase: 100m,
            targetCarryingAmountBase: 100m);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            CreateFxResolution(document),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            target =>
            {
                Assert.Equal(payableAccountId, target.AccountId);
                Assert.Equal(100m, target.TxDebit);
                Assert.Equal(0m, target.TxCredit);
                Assert.Equal(100m, target.Debit);
                Assert.Equal(0m, target.Credit);
                Assert.Equal("settlement:vendor_credit_application_target", target.PostingRole);
                Assert.Equal(1, target.SourceLineNumber);
            },
            source =>
            {
                Assert.Equal(payableAccountId, source.AccountId);
                Assert.Equal(0m, source.TxDebit);
                Assert.Equal(100m, source.TxCredit);
                Assert.Equal(0m, source.Debit);
                Assert.Equal(100m, source.Credit);
                Assert.Equal("settlement:vendor_credit_application_source", source.PostingRole);
                Assert.Equal(1, source.SourceLineNumber);
            });
    }

    [Fact]
    public async Task VendorCreditApplication_ForeignCurrencyLoss_BuildsDebitRealizedLoss()
    {
        var payableAccountId = Guid.NewGuid();
        var gainAccountId = Guid.NewGuid();
        var lossAccountId = Guid.NewGuid();
        var document = CreateVendorCreditApplicationDocument(
            payableAccountId,
            new CurrencyCode("EUR"),
            new CurrencyCode("USD"),
            sourceCarryingAmountBase: 125m,
            targetCarryingAmountBase: 120m,
            gainAccountId,
            lossAccountId);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            CreateFxResolution(document),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            target => Assert.Equal(120m, target.Debit),
            source => Assert.Equal(125m, source.Credit),
            loss =>
            {
                Assert.Equal(lossAccountId, loss.AccountId);
                Assert.Equal(5m, loss.Debit);
                Assert.Equal(0m, loss.Credit);
                Assert.Equal("fx:realized_loss", loss.PostingRole);
                Assert.Equal(1, loss.SourceLineNumber);
            });
    }

    private static CreditApplicationDocument CreateCreditApplicationDocument(
        Guid receivableAccountId,
        CurrencyCode transactionCurrency,
        CurrencyCode baseCurrency,
        decimal sourceCarryingAmountBase,
        decimal targetCarryingAmountBase,
        Guid? gainAccountId = null,
        Guid? lossAccountId = null) =>
        new(
            Guid.NewGuid(),
            CompanyId.FromOrdinal(1),
            EntityNumber.FromLegacy("EN-LEGACY-TEST"),
            new DocumentNumber("CAP-0001"),
            "draft",
            new DateOnly(2026, 4, 12),
            Guid.NewGuid(),
            receivableAccountId,
            gainAccountId,
            lossAccountId,
            transactionCurrency,
            baseCurrency,
            transactionCurrency == baseCurrency
                ? null
                : new FxSnapshotRef(
                    Guid.Empty,
                    baseCurrency,
                    transactionCurrency,
                    1m,
                    new DateOnly(2026, 4, 12),
                    new DateOnly(2026, 4, 12),
                    "subledger_carrying"),
            [
                new CreditApplicationDocumentLine(
                    1,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "Apply customer credit",
                    100m,
                    sourceCarryingAmountBase,
                    targetCarryingAmountBase)
            ],
            totalAmount: 100m,
            memo: "Apply credit note");

    private static VendorCreditApplicationDocument CreateVendorCreditApplicationDocument(
        Guid payableAccountId,
        CurrencyCode transactionCurrency,
        CurrencyCode baseCurrency,
        decimal sourceCarryingAmountBase,
        decimal targetCarryingAmountBase,
        Guid? gainAccountId = null,
        Guid? lossAccountId = null) =>
        new(
            Guid.NewGuid(),
            CompanyId.FromOrdinal(1),
            EntityNumber.FromLegacy("EN-LEGACY-TEST"),
            new DocumentNumber("VCA-0001"),
            "draft",
            new DateOnly(2026, 4, 12),
            Guid.NewGuid(),
            payableAccountId,
            gainAccountId,
            lossAccountId,
            transactionCurrency,
            baseCurrency,
            transactionCurrency == baseCurrency
                ? null
                : new FxSnapshotRef(
                    Guid.Empty,
                    baseCurrency,
                    transactionCurrency,
                    1m,
                    new DateOnly(2026, 4, 12),
                    new DateOnly(2026, 4, 12),
                    "subledger_carrying"),
            [
                new VendorCreditApplicationDocumentLine(
                    1,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "Apply vendor credit",
                    100m,
                    sourceCarryingAmountBase,
                    targetCarryingAmountBase)
            ],
            totalAmount: 100m,
            memo: "Apply vendor credit");

    private static FxResolutionResult CreateFxResolution(IPostingDocument document)
    {
        var snapshot = document switch
        {
            CreditApplicationDocument creditApplication when creditApplication.FxSnapshot is not null => creditApplication.FxSnapshot,
            VendorCreditApplicationDocument vendorCreditApplication when vendorCreditApplication.FxSnapshot is not null => vendorCreditApplication.FxSnapshot,
            _ => new FxSnapshotRef(
                Guid.Empty,
                document.BaseCurrencyCode,
                document.TransactionCurrencyCode,
                1m,
                document.DocumentDate,
                document.DocumentDate,
                "identity")
        };

        return new FxResolutionResult(snapshot, Array.Empty<string>());
    }
}
