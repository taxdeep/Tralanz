using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure.Posting;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class FxRevaluationPostingTests
{
    [Fact]
    public async Task FxRevaluation_ArGain_BuildsDebitArAndCreditUnrealizedGain()
    {
        var gainAccountId = Guid.NewGuid();
        var document = CreateDocument(
            targetOpenItemType: "ar_open_item",
            carryingAmountBase: 120m,
            revaluedAmountBase: 125m,
            unrealizedAmountBase: 5m,
            offsetAccountId: gainAccountId);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Equal("closing", document.FxSnapshot.RateType);
        Assert.Equal("direct", document.FxSnapshot.QuoteBasis);
        Assert.Equal("remeasurement", document.FxSnapshot.RateUseCase);
        Assert.Equal("revaluation", document.FxSnapshot.PostingReason);

        Assert.Collection(
            fragments,
            control =>
            {
                Assert.Equal(document.RevaluationLines[0].TargetControlAccountId, control.AccountId);
                Assert.Equal(5m, control.Debit);
                Assert.Equal(0m, control.Credit);
                Assert.Equal("accounts_receivable", control.ControlRole);
                Assert.Equal("control:accounts_receivable", control.PostingRole);
                Assert.Equal(1, control.SourceLineNumber);
            },
            gain =>
            {
                Assert.Equal(gainAccountId, gain.AccountId);
                Assert.Equal(0m, gain.Debit);
                Assert.Equal(5m, gain.Credit);
                Assert.Equal("fx:unrealized_offset", gain.PostingRole);
                Assert.Equal(1, gain.SourceLineNumber);
            });
    }

    [Fact]
    public async Task FxRevaluation_ApLoss_BuildsDebitUnrealizedLossAndCreditAp()
    {
        var lossAccountId = Guid.NewGuid();
        var document = CreateDocument(
            targetOpenItemType: "ap_open_item",
            carryingAmountBase: 120m,
            revaluedAmountBase: 125m,
            unrealizedAmountBase: 5m,
            offsetAccountId: lossAccountId);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            loss =>
            {
                Assert.Equal(lossAccountId, loss.AccountId);
                Assert.Equal(5m, loss.Debit);
                Assert.Equal(0m, loss.Credit);
                Assert.Equal("fx:unrealized_offset", loss.PostingRole);
                Assert.Equal(1, loss.SourceLineNumber);
            },
            control =>
            {
                Assert.Equal(document.RevaluationLines[0].TargetControlAccountId, control.AccountId);
                Assert.Equal(0m, control.Debit);
                Assert.Equal(5m, control.Credit);
                Assert.Equal("accounts_payable", control.ControlRole);
                Assert.Equal("control:accounts_payable", control.PostingRole);
                Assert.Equal(1, control.SourceLineNumber);
            });
    }

    [Fact]
    public async Task FxRevaluation_ArCreditBalanceLoss_BuildsDebitUnrealizedLossAndCreditAr()
    {
        var lossAccountId = Guid.NewGuid();
        var document = CreateDocument(
            targetOpenItemType: "ar_open_item",
            targetBalanceSide: "credit",
            carryingAmountBase: 120m,
            revaluedAmountBase: 125m,
            unrealizedAmountBase: 5m,
            offsetAccountId: lossAccountId);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            loss =>
            {
                Assert.Equal(lossAccountId, loss.AccountId);
                Assert.Equal(5m, loss.Debit);
                Assert.Equal(0m, loss.Credit);
                Assert.Equal("fx:unrealized_offset", loss.PostingRole);
                Assert.Equal(1, loss.SourceLineNumber);
            },
            control =>
            {
                Assert.Equal(document.RevaluationLines[0].TargetControlAccountId, control.AccountId);
                Assert.Equal(0m, control.Debit);
                Assert.Equal(5m, control.Credit);
                Assert.Equal("accounts_receivable", control.ControlRole);
                Assert.Equal("control:accounts_receivable", control.PostingRole);
                Assert.Equal(1, control.SourceLineNumber);
            });
    }

    [Fact]
    public async Task FxRevaluation_ApDebitBalanceGain_BuildsDebitApAndCreditUnrealizedGain()
    {
        var gainAccountId = Guid.NewGuid();
        var document = CreateDocument(
            targetOpenItemType: "ap_open_item",
            targetBalanceSide: "debit",
            carryingAmountBase: 120m,
            revaluedAmountBase: 125m,
            unrealizedAmountBase: 5m,
            offsetAccountId: gainAccountId);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            control =>
            {
                Assert.Equal(document.RevaluationLines[0].TargetControlAccountId, control.AccountId);
                Assert.Equal(5m, control.Debit);
                Assert.Equal(0m, control.Credit);
                Assert.Equal("accounts_payable", control.ControlRole);
                Assert.Equal("control:accounts_payable", control.PostingRole);
                Assert.Equal(1, control.SourceLineNumber);
            },
            gain =>
            {
                Assert.Equal(gainAccountId, gain.AccountId);
                Assert.Equal(0m, gain.Debit);
                Assert.Equal(5m, gain.Credit);
                Assert.Equal("fx:unrealized_offset", gain.PostingRole);
                Assert.Equal(1, gain.SourceLineNumber);
            });
    }

    [Fact]
    public async Task FxRevaluation_ArNextPeriodUnwind_UsesOriginalGainAccountOnDebit()
    {
        var gainAccountId = Guid.NewGuid();
        var sourceBatchId = Guid.NewGuid();
        var document = CreateDocument(
            targetOpenItemType: "ar_open_item",
            carryingAmountBase: 125m,
            revaluedAmountBase: 120m,
            unrealizedAmountBase: -5m,
            offsetAccountId: gainAccountId,
            batchKind: "next_period_unwind",
            reversalOfDocumentId: sourceBatchId);
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            offset =>
            {
                Assert.Equal(gainAccountId, offset.AccountId);
                Assert.Equal(5m, offset.Debit);
                Assert.Equal(0m, offset.Credit);
            },
            control =>
            {
                Assert.Equal(document.RevaluationLines[0].TargetControlAccountId, control.AccountId);
                Assert.Equal(0m, control.Debit);
                Assert.Equal(5m, control.Credit);
                Assert.Equal("accounts_receivable", control.ControlRole);
            });
    }

    [Fact]
    public async Task FxRevaluation_ApNextPeriodUnwind_UsesOriginalLossAccountOnCredit()
    {
        var lossAccountId = Guid.NewGuid();
        var document = CreateDocument(
            targetOpenItemType: "ap_open_item",
            carryingAmountBase: 125m,
            revaluedAmountBase: 120m,
            unrealizedAmountBase: -5m,
            offsetAccountId: lossAccountId,
            batchKind: "next_period_unwind",
            reversalOfDocumentId: Guid.NewGuid());
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(document.FxSnapshot, Array.Empty<string>()),
            CancellationToken.None);

        Assert.Collection(
            fragments,
            control =>
            {
                Assert.Equal(document.RevaluationLines[0].TargetControlAccountId, control.AccountId);
                Assert.Equal(5m, control.Debit);
                Assert.Equal(0m, control.Credit);
                Assert.Equal("accounts_payable", control.ControlRole);
            },
            offset =>
            {
                Assert.Equal(lossAccountId, offset.AccountId);
                Assert.Equal(0m, offset.Debit);
                Assert.Equal(5m, offset.Credit);
            });
    }

    private static FxRevaluationDocument CreateDocument(
        string targetOpenItemType,
        decimal carryingAmountBase,
        decimal revaluedAmountBase,
        decimal unrealizedAmountBase,
        Guid offsetAccountId,
        string batchKind = "revaluation",
        Guid? reversalOfDocumentId = null,
        string? targetBalanceSide = null) =>
        new(
            Guid.NewGuid(),
            CompanyId.FromOrdinal(1),
            EntityNumber.FromLegacy("EN-LEGACY-TEST"),
            new DocumentNumber("FXRV-0001"),
            "draft",
            new DateOnly(2026, 4, 12),
            new CurrencyCode("EUR"),
            new CurrencyCode("USD"),
            new FxSnapshotRef(
                Guid.NewGuid(),
                new CurrencyCode("USD"),
                new CurrencyCode("EUR"),
                1.25m,
                new DateOnly(2026, 4, 12),
                new DateOnly(2026, 4, 12),
                "company_override",
                "closing",
                "direct",
                "remeasurement",
                "revaluation"),
            unrealizedFxGainAccountId: Guid.NewGuid(),
            unrealizedFxLossAccountId: Guid.NewGuid(),
            lines:
            [
                new FxRevaluationDocumentLine(
                    1,
                    targetOpenItemType,
                    Guid.NewGuid(),
                    targetBalanceSide ?? (targetOpenItemType == "ar_open_item" ? "debit" : "credit"),
                    Guid.NewGuid(),
                    offsetAccountId,
                    Guid.NewGuid(),
                    $"FX revaluation {targetOpenItemType}",
                    openAmountTx: 100m,
                    carryingAmountBase: carryingAmountBase,
                    revaluedAmountBase: revaluedAmountBase,
                    unrealizedAmountBase: unrealizedAmountBase)
            ],
            memo: "Period-end FX revaluation",
            batchKind: batchKind,
            reversalOfDocumentId: reversalOfDocumentId);
}
