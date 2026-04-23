using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure.Posting;

namespace Citus.Accounting.Api.Tests;

public sealed class ReceiptGrIrPostingFragmentBuilderTests
{
    [Fact]
    public async Task BuildAsync_DebitsInventoryAssetAndCreditsGrIrClearing()
    {
        var inventoryAssetAccountId = Guid.NewGuid();
        var grIrClearingAccountId = Guid.NewGuid();
        var document = new ReceiptGrIrPostingDocument(
            Guid.NewGuid(),
            new CompanyId(Guid.NewGuid()),
            new EntityNumber("EN-GRIR-FRAG"),
            new DocumentNumber("GRIR-FRAG"),
            "draft",
            Guid.NewGuid(),
            new DateOnly(2026, 4, 19),
            new CurrencyCode("USD"),
            grIrClearingAccountId,
            new[]
            {
                new ReceiptGrIrPostingDocumentLine(
                    1,
                    Guid.NewGuid(),
                    inventoryAssetAccountId,
                    grIrClearingAccountId,
                    "Receipt GR/IR bridge line 1",
                    25m),
                new ReceiptGrIrPostingDocumentLine(
                    2,
                    Guid.NewGuid(),
                    inventoryAssetAccountId,
                    grIrClearingAccountId,
                    "Receipt GR/IR bridge line 2",
                    15m)
            });
        var builder = new AccountingPostingFragmentBuilder();

        var fragments = await builder.BuildAsync(
            document,
            new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m),
            new FxResolutionResult(
                new FxSnapshotRef(
                    Guid.Empty,
                    new CurrencyCode("USD"),
                    new CurrencyCode("USD"),
                    1m,
                    new DateOnly(2026, 4, 19),
                    new DateOnly(2026, 4, 19),
                    "identity"),
                Array.Empty<string>()),
            CancellationToken.None);

        Assert.Equal(2, fragments.Count);
        Assert.Contains(fragments, fragment =>
            fragment.AccountId == inventoryAssetAccountId &&
            fragment.Debit == 40m &&
            fragment.Credit == 0m &&
            fragment.ControlRole == "inventory_asset" &&
            fragment.PostingRole == "inventory:asset_recognition");
        Assert.Contains(fragments, fragment =>
            fragment.AccountId == grIrClearingAccountId &&
            fragment.Debit == 0m &&
            fragment.Credit == 40m &&
            fragment.ControlRole == "grir_clearing" &&
            fragment.PostingRole == "control:grir_clearing");
    }
}
