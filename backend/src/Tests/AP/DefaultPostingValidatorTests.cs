using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure.Posting;
using SharedKernel.Identity;

namespace Tests.AP;

public sealed class DefaultPostingValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AllowsSubmittedBillDocuments()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var bill = new BillDocument(
            Guid.NewGuid(),
            companyId,
            EntityNumber.Create(2026, 1),
            new DocumentNumber("BILL-VALIDATOR-001"),
            "submitted",
            new DateOnly(2026, 4, 14),
            new DateOnly(2026, 5, 14),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new CurrencyCode("USD"),
            new CurrencyCode("USD"),
            fxSnapshot: null,
            [
                new BillDocumentLine(
                    1,
                    Guid.NewGuid(),
                    "Submitted bill validator regression",
                    64m,
                    taxAmount: 0m,
                    isTaxRecoverable: false,
                    recoverableTaxAccountId: null)
            ],
            subtotalAmount: 64m,
            taxAmount: 0m,
            totalAmount: 64m);

        var validator = new DefaultPostingValidator();
        var context = new PostingContext(
            companyId,
            UserId.FromOrdinal(1),
            new CurrencyCode("USD"),
            AcceptedFxSnapshotId: null,
            IdempotencyKey: "validator-submitted-bill",
            DateTimeOffset.UtcNow);

        await validator.ValidateAsync(bill, context, CancellationToken.None);
    }
}
