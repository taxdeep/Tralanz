using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class ReceiptInventoryActivationStatusPolicyTests
{
    [Theory]
    [InlineData("draft", 1, 0, false, ReceiptInventoryActivationStatusPolicy.NotPosted)]
    [InlineData("posted", 1, 0, false, ReceiptInventoryActivationStatusPolicy.PostedNotActivated)]
    [InlineData("posted", 1, 0, true, ReceiptInventoryActivationStatusPolicy.ActivationFailedRetryable)]
    [InlineData("posted", 2, 2, true, ReceiptInventoryActivationStatusPolicy.Activated)]
    [InlineData("posted", 2, 1, false, ReceiptInventoryActivationStatusPolicy.ActivationInconsistent)]
    public void Resolve_ReturnsStableActivationReadTruth(
        string receiptStatus,
        int receiptLineCount,
        int activatedLineCount,
        bool hasRetryableFailure,
        string expected)
    {
        Assert.Equal(
            expected,
            ReceiptInventoryActivationStatusPolicy.Resolve(
                receiptStatus,
                receiptLineCount,
                activatedLineCount,
                hasRetryableFailure));
    }
}
