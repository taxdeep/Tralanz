using Citus.Modules.Inventory.Domain.Shared;

namespace Infrastructure.PostgreSQL.Inventory;

public static class InventoryAdjustmentJournalPlan
{
    public static IReadOnlyList<InventoryAdjustmentJournalLine> Build(
        InventoryAdjustmentKind adjustmentKind,
        string documentNumber,
        IReadOnlyList<InventoryAdjustmentJournalCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNumber);
        ArgumentNullException.ThrowIfNull(candidates);

        var lines = new List<InventoryAdjustmentJournalLine>(candidates.Count * 2);
        foreach (var candidate in candidates.OrderBy(static candidate => candidate.SourceLineNumber))
        {
            var amountBase = Round6(candidate.AmountBase);
            if (amountBase <= 0m)
            {
                continue;
            }

            var description = $"{documentNumber} line {candidate.SourceLineNumber}: {candidate.ItemCode}";
            switch (adjustmentKind)
            {
                case InventoryAdjustmentKind.Gain:
                    lines.Add(new InventoryAdjustmentJournalLine(
                        candidate.SourceLineNumber,
                        candidate.InventoryAssetAccountId,
                        description,
                        TxDebit: amountBase,
                        TxCredit: 0m,
                        Debit: amountBase,
                        Credit: 0m,
                        PostingRole: "inventory_adjustment:asset_gain"));
                    lines.Add(new InventoryAdjustmentJournalLine(
                        candidate.SourceLineNumber,
                        candidate.AdjustmentAccountId,
                        description,
                        TxDebit: 0m,
                        TxCredit: amountBase,
                        Debit: 0m,
                        Credit: amountBase,
                        PostingRole: "inventory_adjustment:gain_offset"));
                    break;

                case InventoryAdjustmentKind.Loss:
                case InventoryAdjustmentKind.WriteOff:
                    lines.Add(new InventoryAdjustmentJournalLine(
                        candidate.SourceLineNumber,
                        candidate.AdjustmentAccountId,
                        description,
                        TxDebit: amountBase,
                        TxCredit: 0m,
                        Debit: amountBase,
                        Credit: 0m,
                        PostingRole: adjustmentKind == InventoryAdjustmentKind.WriteOff
                            ? "inventory_write_off:loss"
                            : "inventory_adjustment:loss"));
                    lines.Add(new InventoryAdjustmentJournalLine(
                        candidate.SourceLineNumber,
                        candidate.InventoryAssetAccountId,
                        description,
                        TxDebit: 0m,
                        TxCredit: amountBase,
                        Debit: 0m,
                        Credit: amountBase,
                        PostingRole: adjustmentKind == InventoryAdjustmentKind.WriteOff
                            ? "inventory_write_off:asset"
                            : "inventory_adjustment:asset_loss"));
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported inventory adjustment kind '{adjustmentKind}'.");
            }
        }

        var totalDebit = Round6(lines.Sum(static line => line.Debit));
        var totalCredit = Round6(lines.Sum(static line => line.Credit));
        if (totalDebit != totalCredit)
        {
            throw new InvalidOperationException("Inventory adjustment journal plan is not balanced.");
        }

        return lines;
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}

public sealed record InventoryAdjustmentJournalCandidate(
    int SourceLineNumber,
    string ItemCode,
    Guid InventoryAssetAccountId,
    Guid AdjustmentAccountId,
    decimal AmountBase);

public sealed record InventoryAdjustmentJournalLine(
    int SourceLineNumber,
    Guid AccountId,
    string Description,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    string PostingRole);
