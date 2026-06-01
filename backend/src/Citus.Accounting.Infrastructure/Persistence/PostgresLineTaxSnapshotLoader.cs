using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// S5.1: loads a document's per-line tax snapshots from
/// <c>document_line_sales_tax_snapshots</c> so each per-document
/// <c>GetForPostingAsync</c> can attach them to the PostingDocument lines.
/// Returns an empty map when the document was saved with SalesTaxV2 off
/// (no snapshots) — the posting fragment builder then keeps its single
/// <c>line.TaxAmount</c> path. Shared so all six source-doc repositories
/// load snapshots identically.
/// </summary>
internal static class PostgresLineTaxSnapshotLoader
{
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DocumentLineTaxSnapshot>>> LoadAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string documentType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var byLine = new Dictionary<Guid, List<DocumentLineTaxSnapshot>>();

        await using var command = scope.CreateCommand(
            """
            select line_id, sequence, leg, regime_type_snapshot,
                   tax_amount, recoverable_amount, non_recoverable_amount,
                   payable_account_id, recoverable_account_id, non_recoverable_account_id
            from document_line_sales_tax_snapshots
            where company_id = @company_id
              and document_type = @document_type
              and document_id = @document_id
            order by line_id, sequence;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_type", documentType);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var lineId = reader.GetGuid(0);
            if (!byLine.TryGetValue(lineId, out var list))
            {
                list = new List<DocumentLineTaxSnapshot>(1);
                byLine[lineId] = list;
            }

            list.Add(new DocumentLineTaxSnapshot(
                Sequence: reader.GetInt32(1),
                Leg: reader.GetString(2),
                RegimeType: reader.GetString(3),
                TaxAmount: reader.GetDecimal(4),
                RecoverableAmount: reader.GetDecimal(5),
                NonRecoverableAmount: reader.GetDecimal(6),
                PayableAccountId: reader.IsDBNull(7) ? null : reader.GetGuid(7),
                RecoverableAccountId: reader.IsDBNull(8) ? null : reader.GetGuid(8),
                NonRecoverableAccountId: reader.IsDBNull(9) ? null : reader.GetGuid(9)));
        }

        return byLine.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<DocumentLineTaxSnapshot>)kv.Value);
    }

    public static IReadOnlyList<DocumentLineTaxSnapshot> ForLine(
        IReadOnlyDictionary<Guid, IReadOnlyList<DocumentLineTaxSnapshot>> byLine,
        Guid lineId)
        => byLine.TryGetValue(lineId, out var list)
            ? list
            : Array.Empty<DocumentLineTaxSnapshot>();
}
