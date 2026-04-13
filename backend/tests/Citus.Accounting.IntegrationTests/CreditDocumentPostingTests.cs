using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure.Posting;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class CreditDocumentPostingTests
{
    [Fact]
    public async Task CreditNote_BuildsRevenueAndTaxDebitsWithArCredit()
    {
        var receivableAccountId = Guid.NewGuid();
        var revenueAccountId = Guid.NewGuid();
        var taxAccountId = Guid.NewGuid();
        var document = new CreditNoteDocument(
            Guid.NewGuid(),
            new CompanyId(Guid.NewGuid()),
            new EntityNumber("EN2026000017"),
            new DocumentNumber("CN-0001"),
            "draft",
            new DateOnly(2026, 4, 12),
            new DateOnly(2026, 4, 30),
            Guid.NewGuid(),
            receivableAccountId,
            new CurrencyCode("EUR"),
            new CurrencyCode("USD"),
            new FxSnapshotRef(
                Guid.NewGuid(),
                new CurrencyCode("USD"),
                new CurrencyCode("EUR"),
                1.25m,
                new DateOnly(2026, 4, 12),
                new DateOnly(2026, 4, 12),
                "company_override"),
            [
                new CreditNoteDocumentLine(
                    1,
                    revenueAccountId,
                    "Returned goods",
                    1m,
                    100m,
                    100m,
                    20m,
                    taxAccountId)
            ],
            subtotalAmount: 100m,
            taxAmount: 20m,
            totalAmount: 120m,
            memo: "Customer credit");
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot!, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            revenue =>
            {
                Assert.Equal(revenueAccountId, revenue.AccountId);
                Assert.Equal(125m, revenue.Debit);
                Assert.Equal(0m, revenue.Credit);
            },
            tax =>
            {
                Assert.Equal(taxAccountId, tax.AccountId);
                Assert.Equal(25m, tax.Debit);
                Assert.Equal(0m, tax.Credit);
                Assert.Equal("sales_tax_payable", tax.TaxComponentType);
            },
            receivable =>
            {
                Assert.Equal(receivableAccountId, receivable.AccountId);
                Assert.Equal(0m, receivable.Debit);
                Assert.Equal(150m, receivable.Credit);
                Assert.Equal("accounts_receivable", receivable.ControlRole);
            });
    }

    [Fact]
    public async Task VendorCredit_BuildsApDebitWithExpenseAndRecoverableTaxCredits()
    {
        var payableAccountId = Guid.NewGuid();
        var expenseAccountId = Guid.NewGuid();
        var recoverableTaxAccountId = Guid.NewGuid();
        var document = new VendorCreditDocument(
            Guid.NewGuid(),
            new CompanyId(Guid.NewGuid()),
            new EntityNumber("EN2026000018"),
            new DocumentNumber("VC-0001"),
            "draft",
            new DateOnly(2026, 4, 12),
            new DateOnly(2026, 4, 30),
            Guid.NewGuid(),
            payableAccountId,
            new CurrencyCode("EUR"),
            new CurrencyCode("USD"),
            new FxSnapshotRef(
                Guid.NewGuid(),
                new CurrencyCode("USD"),
                new CurrencyCode("EUR"),
                1.25m,
                new DateOnly(2026, 4, 12),
                new DateOnly(2026, 4, 12),
                "company_override"),
            [
                new VendorCreditDocumentLine(
                    1,
                    expenseAccountId,
                    "Supplier rebate",
                    100m,
                    20m,
                    true,
                    recoverableTaxAccountId)
            ],
            subtotalAmount: 100m,
            taxAmount: 20m,
            totalAmount: 120m,
            memo: "Vendor credit");
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot!, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            payable =>
            {
                Assert.Equal(payableAccountId, payable.AccountId);
                Assert.Equal(150m, payable.Debit);
                Assert.Equal(0m, payable.Credit);
                Assert.Equal("accounts_payable", payable.ControlRole);
            },
            expense =>
            {
                Assert.Equal(expenseAccountId, expense.AccountId);
                Assert.Equal(0m, expense.Debit);
                Assert.Equal(125m, expense.Credit);
            },
            recoverableTax =>
            {
                Assert.Equal(recoverableTaxAccountId, recoverableTax.AccountId);
                Assert.Equal(0m, recoverableTax.Debit);
                Assert.Equal(25m, recoverableTax.Credit);
                Assert.Equal("purchase_tax_recoverable", recoverableTax.TaxComponentType);
            });
    }
}
