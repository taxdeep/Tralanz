using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Journal;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure.Posting;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class ForeignCurrencySettlementPostingTests
{
    [Fact]
    public async Task ReceivePayment_ForeignCurrency_BuildsRealizedGainFragment()
    {
        var document = CreateReceivePaymentDocument(
            appliedAmountBase: 125m,
            carryingAmountBase: 120m);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot!, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            bank =>
            {
                Assert.Equal(document.BankAccountId, bank.AccountId);
                Assert.Equal(100m, bank.TxDebit);
                Assert.Equal(125m, bank.Debit);
                Assert.Equal(0m, bank.Credit);
            },
            ar =>
            {
                Assert.Equal(document.ReceivableAccountId, ar.AccountId);
                Assert.Equal(100m, ar.TxCredit);
                Assert.Equal(120m, ar.Credit);
                Assert.Equal("accounts_receivable", ar.ControlRole);
            },
            fxGain =>
            {
                Assert.Equal(document.RealizedFxGainAccountId!.Value, fxGain.AccountId);
                Assert.Equal(0m, fxGain.TxDebit);
                Assert.Equal(0m, fxGain.TxCredit);
                Assert.Equal(5m, fxGain.Credit);
            });
    }

    [Fact]
    public async Task PayBill_ForeignCurrency_BuildsRealizedLossFragment()
    {
        var document = CreatePayBillDocument(
            appliedAmountBase: 125m,
            carryingAmountBase: 120m);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot!, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            ap =>
            {
                Assert.Equal(document.PayableAccountId, ap.AccountId);
                Assert.Equal(100m, ap.TxDebit);
                Assert.Equal(120m, ap.Debit);
                Assert.Equal("accounts_payable", ap.ControlRole);
            },
            bank =>
            {
                Assert.Equal(document.BankAccountId, bank.AccountId);
                Assert.Equal(100m, bank.TxCredit);
                Assert.Equal(125m, bank.Credit);
            },
            fxLoss =>
            {
                Assert.Equal(document.RealizedFxLossAccountId!.Value, fxLoss.AccountId);
                Assert.Equal(5m, fxLoss.Debit);
                Assert.Equal(0m, fxLoss.Credit);
            });
    }

    [Fact]
    public async Task PostingEngine_UsesEmbeddedSourceDocumentFxSnapshot_ForForeignCurrencySettlement()
    {
        var document = CreateReceivePaymentDocument(
            appliedAmountBase: 125m,
            carryingAmountBase: 120m);
        var builder = new CapturingFragmentBuilder();
        var fxService = new ThrowingFxResolutionService();
        var engine = new DefaultPostingEngine(
            new NoOpPostingValidator(),
            new NoOpTaxEngine(),
            fxService,
            builder,
            new StubJournalAggregator(),
            new StubJournalEntryWriter());

        var result = await engine.PostAsync(
            document,
            new PostingContext(
                document.CompanyId,
                new UserId(Guid.NewGuid()),
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                IdempotencyKey: null,
                RequestedAt: DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(0, fxService.CallCount);
        Assert.NotNull(builder.CapturedFxResult);
        Assert.Equal(document.FxSnapshot!.Rate, builder.CapturedFxResult!.Snapshot.Rate);
        Assert.Equal("posted", result.Status);
    }

    private static ReceivePaymentDocument CreateReceivePaymentDocument(
        decimal appliedAmountBase,
        decimal carryingAmountBase)
    {
        var companyId = new CompanyId(Guid.NewGuid());
        return new ReceivePaymentDocument(
            Guid.NewGuid(),
            companyId,
            new EntityNumber("EN2026000001"),
            new DocumentNumber("RP-0001"),
            "draft",
            new DateOnly(2026, 4, 12),
            customerId: Guid.NewGuid(),
            bankAccountId: Guid.NewGuid(),
            receivableAccountId: Guid.NewGuid(),
            realizedFxGainAccountId: Guid.NewGuid(),
            realizedFxLossAccountId: Guid.NewGuid(),
            transactionCurrencyCode: new CurrencyCode("EUR"),
            baseCurrencyCode: new CurrencyCode("USD"),
            fxSnapshot: CreateFxSnapshot(rate: 1.25m),
            lines:
            [
                new ReceivePaymentDocumentLine(
                    1,
                    Guid.NewGuid(),
                    "Receive payment application line 1",
                    appliedAmount: 100m,
                    appliedAmountBase: appliedAmountBase,
                    carryingAmountBase: carryingAmountBase)
            ],
            totalAmount: 100m,
            memo: "Foreign-currency receive payment");
    }

    private static PayBillDocument CreatePayBillDocument(
        decimal appliedAmountBase,
        decimal carryingAmountBase)
    {
        var companyId = new CompanyId(Guid.NewGuid());
        return new PayBillDocument(
            Guid.NewGuid(),
            companyId,
            new EntityNumber("EN2026000002"),
            new DocumentNumber("PB-0001"),
            "draft",
            new DateOnly(2026, 4, 12),
            vendorId: Guid.NewGuid(),
            bankAccountId: Guid.NewGuid(),
            payableAccountId: Guid.NewGuid(),
            realizedFxGainAccountId: Guid.NewGuid(),
            realizedFxLossAccountId: Guid.NewGuid(),
            transactionCurrencyCode: new CurrencyCode("EUR"),
            baseCurrencyCode: new CurrencyCode("USD"),
            fxSnapshot: CreateFxSnapshot(rate: 1.25m),
            lines:
            [
                new PayBillDocumentLine(
                    1,
                    Guid.NewGuid(),
                    "Pay bill application line 1",
                    appliedAmount: 100m,
                    appliedAmountBase: appliedAmountBase,
                    carryingAmountBase: carryingAmountBase)
            ],
            totalAmount: 100m,
            memo: "Foreign-currency pay bill");
    }

    private static FxSnapshotRef CreateFxSnapshot(decimal rate) =>
        new(
            Guid.NewGuid(),
            new CurrencyCode("USD"),
            new CurrencyCode("EUR"),
            rate,
            new DateOnly(2026, 4, 12),
            new DateOnly(2026, 4, 12),
            "company_override");

    private sealed class NoOpPostingValidator : IPostingValidator
    {
        public Task ValidateAsync(
            IPostingDocument document,
            PostingContext context,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpTaxEngine : ITaxEngine
    {
        public Task<TaxComputationResult> CalculateAsync(
            IPostingDocument document,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m));
    }

    private sealed class ThrowingFxResolutionService : IFxResolutionService
    {
        public int CallCount { get; private set; }

        public Task<FxResolutionResult> ResolveAsync(
            FxResolutionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Embedded FX snapshot should have bypassed external resolution.");
        }
    }

    private sealed class CapturingFragmentBuilder : IPostingFragmentBuilder
    {
        public FxResolutionResult? CapturedFxResult { get; private set; }

        public Task<IReadOnlyList<PostingFragment>> BuildAsync(
            IPostingDocument document,
            TaxComputationResult taxResult,
            FxResolutionResult fxResult,
            CancellationToken cancellationToken)
        {
            CapturedFxResult = fxResult;
            return Task.FromResult<IReadOnlyList<PostingFragment>>(
                [
                    new PostingFragment(
                        Guid.NewGuid(),
                        document.TransactionCurrencyCode,
                        0m,
                        0m,
                        0m,
                        0m,
                        "No-op")
                ]);
        }
    }

    private sealed class StubJournalAggregator : IJournalAggregator
    {
        public JournalEntryDraft Aggregate(
            IPostingDocument document,
            IReadOnlyList<PostingFragment> fragments,
            FxResolutionResult fxResult) =>
            new(
                document.CompanyId,
                document.SourceType,
                document.Id,
                document.TransactionCurrencyCode,
                document.BaseCurrencyCode,
                fxResult.Snapshot,
                [
                    new JournalEntryDraftLine(
                        1,
                        Guid.NewGuid(),
                        "No-op",
                        0m,
                        0m,
                        0m,
                        0m)
                ]);
    }

    private sealed class StubJournalEntryWriter : IJournalEntryWriter
    {
        public Task<JournalEntryWriteResult> WriteAsync(
            JournalEntryDraft draft,
            PostingContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new JournalEntryWriteResult(Guid.NewGuid(), "JE-TEST"));
    }
}
